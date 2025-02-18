using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Collections.Immutable;
using System.Linq;
using System.Text;
using Pine;

namespace ElmFullstack
{
    public record ElmAppInterfaceConfig(string RootModuleName)
    {
        static public ElmAppInterfaceConfig Default => new
        (
            RootModuleName: "Backend.Main"
        );
    }

    public struct ElmAppInterfaceConvention
    {
        public const string InitialStateFunctionName = "interfaceToHost_initState";

        public const string ProcessSerializedEventFunctionName = "interfaceToHost_processEvent";

        public const string SerializeStateFunctionName = "interfaceToHost_serializeState";

        public const string DeserializeStateFunctionName = "interfaceToHost_deserializeState";

        static public IImmutableList<string> CompilationInterfaceModuleNamePrefixes => ImmutableList.Create("ElmFullstackCompilerInterface", "CompilationInterface");
    }

    public class ElmAppCompilation
    {
        static readonly System.Diagnostics.Stopwatch cacheItemTimeSource = System.Diagnostics.Stopwatch.StartNew();

        static readonly ConcurrentDictionary<string, (Result<IReadOnlyList<LocatedCompilationError>, CompilationSuccess> compilationResult, TimeSpan lastUseTime)> ElmAppCompilationCache = new();

        static void ElmAppCompilationCacheRemoveOlderItems(long retainedSizeLimit) =>
            Cache.RemoveItemsToLimitRetainedSize(
                ElmAppCompilationCache,
                item => 1000 + EstimateCacheItemSizeInMemory(item.Value.compilationResult),
                item => item.Value.lastUseTime,
                retainedSizeLimit);

        static public Result<IReadOnlyList<LocatedCompilationError>, CompilationSuccess> AsCompletelyLoweredElmApp(
            IImmutableDictionary<IImmutableList<string>, IReadOnlyList<byte>> sourceFiles,
            ElmAppInterfaceConfig interfaceConfig)
        {
            var sourceFilesHash =
                CommonConversion.StringBase16FromByteArray(Composition.GetHash(Composition.SortedTreeFromSetOfBlobsWithStringPath(sourceFiles)));

            var compilationHash =
                CommonConversion.StringBase16FromByteArray(CommonConversion.HashSHA256(Encoding.UTF8.GetBytes(
                    Newtonsoft.Json.JsonConvert.SerializeObject(new
                    {
                        sourceFilesHash,
                        interfaceConfig,
                    }))));

            Result<IReadOnlyList<LocatedCompilationError>, CompilationSuccess> compileNew() =>
                    AsCompletelyLoweredElmApp(
                        sourceFiles,
                        rootModuleName: interfaceConfig.RootModuleName.Split('.').ToImmutableList(),
                        interfaceToHostRootModuleName: InterfaceToHostRootModuleName.Split('.').ToImmutableList());

            (Result<IReadOnlyList<LocatedCompilationError>, CompilationSuccess> compilationResult, TimeSpan lastUseTime) BuildNextCacheEntry(
                Result<IReadOnlyList<LocatedCompilationError>, CompilationSuccess> previousEntryCompilationResult)
            {
                if (previousEntryCompilationResult?.Ok != null)
                    return (previousEntryCompilationResult, cacheItemTimeSource.Elapsed);

                return (compileNew(), cacheItemTimeSource.Elapsed);
            }

            var (compilationResult, _) =
                ElmAppCompilationCache.AddOrUpdate(
                    compilationHash,
                    _ => BuildNextCacheEntry(null),
                    (_, previousEntry) => BuildNextCacheEntry(previousEntry.compilationResult));

            ElmAppCompilationCacheRemoveOlderItems(50_000_000);

            return compilationResult;
        }

        public record CompilationSuccess(
            IImmutableDictionary<IImmutableList<string>, IReadOnlyList<byte>> compiledAppFiles,
            IImmutableList<CompilationIterationReport> iterationsReports);

        static Result<IReadOnlyList<LocatedCompilationError>, CompilationSuccess> AsCompletelyLoweredElmApp(
            IImmutableDictionary<IImmutableList<string>, IReadOnlyList<byte>> sourceFiles,
            IImmutableList<string> rootModuleName,
            IImmutableList<string> interfaceToHostRootModuleName) =>
            AsCompletelyLoweredElmApp(
                sourceFiles,
                rootModuleName,
                interfaceToHostRootModuleName,
                ImmutableStack<(IImmutableList<(CompilerSerialInterface.DependencyKey key, IReadOnlyList<byte> value)> discoveredDependencies, CompilationIterationReport previousIterationsReports)>.Empty);

        static Result<IReadOnlyList<LocatedCompilationError>, CompilationSuccess> AsCompletelyLoweredElmApp(
            IImmutableDictionary<IImmutableList<string>, IReadOnlyList<byte>> sourceFiles,
            IImmutableList<string> rootModuleName,
            IImmutableList<string> interfaceToHostRootModuleName,
            IImmutableStack<(IImmutableList<(CompilerSerialInterface.DependencyKey key, IReadOnlyList<byte> value)> discoveredDependencies, CompilationIterationReport iterationReport)> stack)
        {
            if (10 < stack.Count())
                throw new Exception("Iteration stack depth > 10");

            var totalStopwatch = System.Diagnostics.Stopwatch.StartNew();

            var dependencies =
                stack.SelectMany(frame => frame.discoveredDependencies)
                .ToImmutableList();

            var (compilationResult, compilationReport) = CachedElmAppCompilationIteration(
                compilerElmProgramCodeFiles: CachedCompilerElmProgramCodeFilesForElmFullstackBackend.Value,
                sourceFiles: sourceFiles,
                rootModuleName: rootModuleName,
                interfaceToHostRootModuleName: interfaceToHostRootModuleName,
                dependencies: dependencies);

            var compilationSuccess = compilationResult.Ok?.FirstOrDefault();

            var currentIterationReport = new CompilationIterationReport
            (
                compilation: compilationReport,
                dependenciesReports: null,
                totalTimeSpentMilli: (int)totalStopwatch.ElapsedMilliseconds
            );

            if (compilationSuccess != null)
            {
                return new Result<IReadOnlyList<LocatedCompilationError>, CompilationSuccess>(
                    Ok: new CompilationSuccess(compilationSuccess, stack.Select(frame => frame.iterationReport).ToImmutableList().Add(currentIterationReport)));
            }

            var compilationErrors =
                compilationResult.Err?.FirstOrDefault();

            if (compilationErrors == null)
                throw new Exception("Failed compilation: Protocol error: Missing error descriptions.");

            var compilationErrorsMissingElmMakeDependencies =
                compilationErrors
                .Where(error => error.error?.MissingDependencyError?.FirstOrDefault().ElmMakeDependency != null)
                .ToImmutableList();

            var otherErrors =
                compilationErrors.Except(compilationErrorsMissingElmMakeDependencies)
                .ToImmutableList();

            if (0 < otherErrors.Count)
            {
                return new Result<IReadOnlyList<LocatedCompilationError>, CompilationSuccess>(
                    Err: otherErrors.Select(error => new LocatedCompilationError(error.location, error: CompilationError.AsCompilationError(error.error))).ToImmutableList());
            }

            byte[] ElmMake(
                IImmutableDictionary<IImmutableList<string>, IReadOnlyList<byte>> elmCodeFiles,
                IImmutableList<string> pathToFileWithElmEntryPoint,
                bool makeJavascript,
                bool enableDebug)
            {
                var elmMakeCommandAppendix =
                    enableDebug ? "--debug" : null;

                var fileAsString = ProcessFromElm019Code.CompileElm(
                    elmCodeFiles,
                    pathToFileWithElmEntryPoint: pathToFileWithElmEntryPoint,
                    outputFileName: "output." + (makeJavascript ? "js" : "html"),
                    elmMakeCommandAppendix: elmMakeCommandAppendix);

                return Encoding.UTF8.GetBytes(fileAsString);
            }

            var newDependencies =
                compilationErrorsMissingElmMakeDependencies
                .Select(error =>
                {
                    var dependencyTotalStopwatch = System.Diagnostics.Stopwatch.StartNew();

                    var dependencyKey =
                        error.error.MissingDependencyError.FirstOrDefault();

                    var elmMakeRequest =
                        dependencyKey.ElmMakeDependency.FirstOrDefault();

                    TimedReport<CompilationIterationDependencyReport> completeDependencyReport(CompilationIterationDependencyReport dependencyReport) =>
                        new(report: dependencyReport, totalTimeSpentMilli: (int)dependencyTotalStopwatch.ElapsedMilliseconds);

                    if (elmMakeRequest != null)
                    {
                        Result<string, IReadOnlyList<byte>> buildResultValue()
                        {
                            try
                            {
                                var elmMakeRequestFiles =
                                    elmMakeRequest.files
                                    .ToImmutableDictionary(
                                        entry => (IImmutableList<string>)entry.path.ToImmutableList(),
                                        entry => (IReadOnlyList<byte>)Convert.FromBase64String(entry.content.AsBase64))
                                    .WithComparers(EnumerableExtension.EqualityComparer<string>());

                                var value = ElmMake(
                                    elmCodeFiles: elmMakeRequestFiles,
                                    pathToFileWithElmEntryPoint: elmMakeRequest.entryPointFilePath.ToImmutableList(),
                                    makeJavascript: elmMakeRequest.outputType.ElmMakeOutputTypeJs != null,
                                    enableDebug: elmMakeRequest.enableDebug);

                                return Result<string, IReadOnlyList<byte>>.ok(value);
                            }
                            catch (Exception e)
                            {
                                return Result<string, IReadOnlyList<byte>>.err("Failed with runtime exception: " + e.ToString());
                            }
                        }

                        return (
                            (key: dependencyKey, result: buildResultValue()),
                            completeDependencyReport(new CompilationIterationDependencyReport(dependencyKeySummary: "ElmMake")));
                    }

                    throw new Exception("Protocol error: Unknown type of dependency: " + DescribeCompilationError(error));
                })
                .ToImmutableList();

            currentIterationReport =
                currentIterationReport with
                {
                    dependenciesReports = newDependencies.Select(depAndReport => depAndReport.Item2).ToImmutableList(),
                    totalTimeSpentMilli = (int)totalStopwatch.ElapsedMilliseconds
                };

            var newDependenciesWithError =
                newDependencies.Where(dep => dep.Item1.result.Ok == null).ToImmutableList();

            /*
             * TODO: Instead of returning here, probably forward the result back into the compiler for packaging, for example adding location info.
             * This implies expansion of the dependencies API to model the error cases.
             * */
            if (0 < newDependenciesWithError.Count)
            {
                return Result<IReadOnlyList<LocatedCompilationError>, CompilationSuccess>.err(
                    newDependenciesWithError.Select(dep => new LocatedCompilationError(
                        location: null,
                        error: new CompilationError(DependencyError: dep.Item2.report.dependencyKeySummary + " " + dep.Item1.result.Err)))
                    .ToImmutableList());
            }

            var newStackFrame =
                (newDependencies.Select(depAndReport => (depAndReport.Item1.key, depAndReport.Item1.result.Ok)).ToImmutableList(), currentIterationReport);

            return AsCompletelyLoweredElmApp(
                sourceFiles: sourceFiles,
                rootModuleName,
                interfaceToHostRootModuleName,
                stack: stack.Push(newStackFrame));
        }

        static readonly ConcurrentDictionary<string, (ElmValueCommonJson.Result<IReadOnlyList<CompilerSerialInterface.LocatedCompilationError>, ImmutableDictionary<IImmutableList<string>, IReadOnlyList<byte>>> compilationResult, TimeSpan lastUseTime)> ElmAppCompilationIterationCache = new();

        static void ElmAppCompilationIterationCacheRemoveOlderItems(long retainedSizeLimit) =>
            Cache.RemoveItemsToLimitRetainedSize(
                ElmAppCompilationIterationCache,
                item => 1000 + EstimateCacheItemSizeInMemory(item.Value.compilationResult),
                item => item.Value.lastUseTime,
                retainedSizeLimit);

        static (ElmValueCommonJson.Result<IReadOnlyList<CompilerSerialInterface.LocatedCompilationError>, ImmutableDictionary<IImmutableList<string>, IReadOnlyList<byte>>>, CompilationIterationCompilationReport report) CachedElmAppCompilationIteration(
            IImmutableDictionary<IImmutableList<string>, IReadOnlyList<byte>> compilerElmProgramCodeFiles,
            IImmutableDictionary<IImmutableList<string>, IReadOnlyList<byte>> sourceFiles,
            IImmutableList<string> rootModuleName,
            IImmutableList<string> interfaceToHostRootModuleName,
            ImmutableList<(CompilerSerialInterface.DependencyKey key, IReadOnlyList<byte> value)> dependencies)
        {
            var totalStopwatch = System.Diagnostics.Stopwatch.StartNew();
            var serializeStopwatch = System.Diagnostics.Stopwatch.StartNew();

            var sourceFilesJson =
                sourceFiles
                .Select(appCodeFile => new CompilerSerialInterface.AppCodeEntry
                (
                    path: appCodeFile.Key,
                    content: CompilerSerialInterface.BytesJson.AsJson(appCodeFile.Value)
                ))
                .ToImmutableList();

            var dependenciesJson =
                dependencies
                .Select(dependency =>
                new
                {
                    key = dependency.key,
                    value = CompilerSerialInterface.BytesJson.AsJson(dependency.value),
                })
                .ToImmutableList();

            var argumentsJson = Newtonsoft.Json.JsonConvert.SerializeObject(
                new
                {
                    sourceFiles = sourceFilesJson,
                    compilationInterfaceElmModuleNamePrefixes = ElmAppInterfaceConvention.CompilationInterfaceModuleNamePrefixes,
                    dependencies = dependenciesJson,
                    rootModuleName = rootModuleName,
                    interfaceToHostRootModuleName = interfaceToHostRootModuleName,
                }
            );

            var argumentsJsonHash =
                CommonConversion.StringBase16FromByteArray(CommonConversion.HashSHA256(Encoding.UTF8.GetBytes(argumentsJson)));

            serializeStopwatch.Stop();

            System.Diagnostics.Stopwatch prepareJsEngineStopwatch = null;
            System.Diagnostics.Stopwatch inJsEngineStopwatch = null;
            System.Diagnostics.Stopwatch deserializeStopwatch = null;

            ElmValueCommonJson.Result<IReadOnlyList<CompilerSerialInterface.LocatedCompilationError>, ImmutableDictionary<IImmutableList<string>, IReadOnlyList<byte>>> compileNew()
            {
                prepareJsEngineStopwatch = System.Diagnostics.Stopwatch.StartNew();

                var jsEngine = CachedJsEngineToCompileFileTree(compilerElmProgramCodeFiles);

                prepareJsEngineStopwatch.Stop();

                inJsEngineStopwatch = System.Diagnostics.Stopwatch.StartNew();

                var responseJson =
                    jsEngine.CallFunction("lowerSerialized", argumentsJson)
                    ?.ToString();

                inJsEngineStopwatch.Stop();

                deserializeStopwatch = System.Diagnostics.Stopwatch.StartNew();

                var compilationResponse =
                    Newtonsoft.Json.JsonConvert.DeserializeObject<
                        ElmValueCommonJson.Result<
                            string,
                            ElmValueCommonJson.Result<
                                IReadOnlyList<CompilerSerialInterface.LocatedCompilationError>,
                                IReadOnlyList<CompilerSerialInterface.AppCodeEntry>>>>(responseJson);

                if (compilationResponse.Ok?.FirstOrDefault() == null)
                    throw new Exception("Protocol error: " + compilationResponse.Err);

                var mappedResult =
                    compilationResponse.Ok?.FirstOrDefault().map(files =>
                        files.ToImmutableDictionary(
                            entry => (IImmutableList<string>)entry.path.ToImmutableList(),
                            entry => (IReadOnlyList<byte>)Convert.FromBase64String(entry.content.AsBase64))
                        .WithComparers(EnumerableExtension.EqualityComparer<string>()));

                return mappedResult;
            }

            var result =
                ElmAppCompilationIterationCache.AddOrUpdate(
                    argumentsJsonHash,
                    _ => (compileNew(), cacheItemTimeSource.Elapsed),
                    (_, previousEntry) => (previousEntry.compilationResult, cacheItemTimeSource.Elapsed));

            ElmAppCompilationIterationCacheRemoveOlderItems(50_000_000);

            return
                (result.compilationResult,
                new CompilationIterationCompilationReport
                (
                    serializeTimeSpentMilli: (int)serializeStopwatch.ElapsedMilliseconds,
                    prepareJsEngineTimeSpentMilli: (int?)prepareJsEngineStopwatch?.ElapsedMilliseconds,
                    inJsEngineTimeSpentMilli: (int?)inJsEngineStopwatch?.ElapsedMilliseconds,
                    deserializeTimeSpentMilli: (int?)deserializeStopwatch?.ElapsedMilliseconds,
                    totalTimeSpentMilli: (int)totalStopwatch.ElapsedMilliseconds
                ));
        }

        static public IImmutableList<string> FilePathFromModuleName(IReadOnlyList<string> moduleName)
        {
            var fileName = moduleName.Last() + ".elm";
            var directoryNames = moduleName.Reverse().Skip(1).Reverse();

            return new[] { "src" }.Concat(directoryNames).Concat(new[] { fileName }).ToImmutableList();
        }

        static public IImmutableList<string> FilePathFromModuleName(string moduleName) =>
            FilePathFromModuleName(moduleName.Split(new[] { '.' }).ToImmutableList());

        static public string InterfaceToHostRootModuleName => "Backend.InterfaceToHost_Root";

        static public JavaScriptEngineSwitcher.Core.IJsEngine CachedJsEngineToCompileFileTree(
            IImmutableDictionary<IImmutableList<string>, IReadOnlyList<byte>> compilerElmProgramCodeFiles)
        {
            var compilerId =
                CommonConversion.StringBase16FromByteArray(
                    Composition.GetHash(
                        Composition.FromTreeWithStringPath(
                            Composition.SortedTreeFromSetOfBlobsWithStringPath(compilerElmProgramCodeFiles))));

            return FileTreeCompilerJsEngineCache.GetOrAdd(
                compilerId,
                _ => CreateJsEngineToCompileFileTree(compilerElmProgramCodeFiles));
        }

        static readonly ConcurrentDictionary<string, JavaScriptEngineSwitcher.Core.IJsEngine> FileTreeCompilerJsEngineCache = new();

        static public JavaScriptEngineSwitcher.Core.IJsEngine CreateJsEngineToCompileFileTree(
            IImmutableDictionary<IImmutableList<string>, IReadOnlyList<byte>> compilerElmProgramCodeFiles)
        {
            var javascript = BuildJavascriptToCompileFileTree(compilerElmProgramCodeFiles);

            var javascriptEngine = ProcessHostedWithV8.ConstructJsEngine();

            javascriptEngine.Evaluate(javascript);

            return javascriptEngine;
        }

        static string BuildJavascriptToCompileFileTree(IImmutableDictionary<IImmutableList<string>, IReadOnlyList<byte>> compilerElmProgramCodeFiles)
        {
            var javascriptFromElmMake =
                ProcessFromElm019Code.CompileElmToJavascript(
                    compilerElmProgramCodeFiles,
                    ImmutableList.Create("src", "Main.elm"));

            var javascriptMinusCrashes = ProcessFromElm019Code.JavascriptMinusCrashes(javascriptFromElmMake);

            var listFunctionToPublish =
                new[]
                {
                    (functionNameInElm: "Main.lowerSerialized",
                    publicName: "lowerSerialized",
                    arity: 1),
                };

            return
                ProcessFromElm019Code.PublishFunctionsFromJavascriptFromElmMake(
                    javascriptMinusCrashes,
                    listFunctionToPublish);
        }

        static readonly public Lazy<IImmutableDictionary<IImmutableList<string>, IReadOnlyList<byte>>> CachedCompilerElmProgramCodeFilesForElmFullstackBackend =
            new(LoadCompilerElmProgramCodeFilesForElmFullstackBackend);

        static public IImmutableDictionary<IImmutableList<string>, IReadOnlyList<byte>> LoadCompilerElmProgramCodeFilesForElmFullstackBackend() =>
            ImmutableDictionary<IImmutableList<string>, IReadOnlyList<byte>>.Empty
            .WithComparers(EnumerableExtension.EqualityComparer<string>())
            .SetItem(ImmutableList.Create("elm.json"), GetManifestResourceStreamContent("elm_fullstack.ElmFullstack.compile_elm_program.elm.json"))
            .SetItem(ImmutableList.Create("src", "FileTree.elm"), GetManifestResourceStreamContent("elm_fullstack.ElmFullstack.compile_elm_program.src.FileTree.elm"))
            .SetItem(ImmutableList.Create("src", "CompileFullstackApp.elm"), GetManifestResourceStreamContent("elm_fullstack.ElmFullstack.compile_elm_program.src.CompileFullstackApp.elm"))
            .SetItem(ImmutableList.Create("src", "Main.elm"), GetManifestResourceStreamContent("elm_fullstack.ElmFullstack.compile_elm_program.src.Main.elm"));

        static byte[] GetManifestResourceStreamContent(string name)
        {
            using var stream = typeof(ElmAppCompilation).Assembly.GetManifestResourceStream(name);
            using var memoryStream = new System.IO.MemoryStream();

            stream.CopyTo(memoryStream);

            return memoryStream.ToArray();
        }

        static public string CompileCompilationErrorsDisplayText(IReadOnlyList<LocatedCompilationError> compilationErrors)
        {
            var errorsText =
                string.Join("\n\n", compilationErrors?.Select(DescribeCompilationError));

            return "Compilation failed with " + compilationErrors?.Count + " error" + (compilationErrors?.Count == 1 ? "" : "s") + ":\n\n" + errorsText;
        }

        static public string DescribeCompilationError(LocatedCompilationError locatedCompilationError) =>
            (locatedCompilationError.location == null ? "without location information" :
            "in file " + string.Join('/', locatedCompilationError.location.filePath)) + ": " +
            DescribeCompilationError(locatedCompilationError.error);

        static string DescribeCompilationError(CompilerSerialInterface.LocatedCompilationError locatedCompilationError) =>
            "in file " + string.Join('/', locatedCompilationError.location.filePath) + ": " +
            DescribeCompilationError(locatedCompilationError.error);

        static string DescribeCompilationError(CompilationError compilationError)
        {
            if (compilationError.OtherError != null)
                return compilationError.OtherError;

            if (compilationError.DependencyError != null)
                return "Dependency Error: " + compilationError.DependencyError;

            throw new Exception("Unexpected shape of value " +
                Newtonsoft.Json.JsonConvert.SerializeObject(
                    compilationError,
                    Newtonsoft.Json.Formatting.Indented,
                    new Newtonsoft.Json.JsonSerializerSettings
                    {
                        NullValueHandling = Newtonsoft.Json.NullValueHandling.Ignore,
                        DefaultValueHandling = Newtonsoft.Json.DefaultValueHandling.Include
                    }));
        }

        static string DescribeCompilationError(CompilerSerialInterface.CompilationError compilationError)
        {
            var otherCompilationError =
                compilationError?.OtherCompilationError?.FirstOrDefault();

            if (otherCompilationError != null)
                return otherCompilationError;

            return
                Newtonsoft.Json.JsonConvert.SerializeObject(
                    compilationError,
                    Newtonsoft.Json.Formatting.Indented,
                    new Newtonsoft.Json.JsonSerializerSettings
                    {
                        NullValueHandling = Newtonsoft.Json.NullValueHandling.Ignore,
                        DefaultValueHandling = Newtonsoft.Json.DefaultValueHandling.Include
                    });
        }

        static long EstimateCacheItemSizeInMemory(ElmValueCommonJson.Result<IReadOnlyList<CompilerSerialInterface.LocatedCompilationError>, ImmutableDictionary<IImmutableList<string>, IReadOnlyList<byte>>> item) =>
               (item.Err?.Sum(err => err.Sum(EstimateCacheItemSizeInMemory)) ?? 0) +
               (item.Ok?.Sum(EstimateCacheItemSizeInMemory) ?? 0);

        static long EstimateCacheItemSizeInMemory(CompilerSerialInterface.LocatedCompilationError compilationError) =>
            100 + (compilationError.location?.filePath.Sum(e => e.Length) ?? 0) + EstimateCacheItemSizeInMemory(compilationError.error);

        static long EstimateCacheItemSizeInMemory(CompilerSerialInterface.CompilationError compilationError) =>
            compilationError?.MissingDependencyError?.Sum(EstimateCacheItemSizeInMemory) ?? 0;

        static long EstimateCacheItemSizeInMemory(Result<IReadOnlyList<LocatedCompilationError>, CompilationSuccess> item) =>
            (item.Err?.Sum(err => EstimateCacheItemSizeInMemory(err)) ?? 0) +
            (item.Ok != null ? EstimateCacheItemSizeInMemory(item.Ok) : 0);

        static long EstimateCacheItemSizeInMemory(CompilationSuccess compilationSuccess) =>
            EstimateCacheItemSizeInMemory(compilationSuccess.compiledAppFiles);

        static long EstimateCacheItemSizeInMemory(IImmutableDictionary<IImmutableList<string>, IReadOnlyList<byte>> item) =>
            (item?.Sum(file => file.Key.Sum(e => e.Length) + file.Value.Count)) ?? 0;

        static long EstimateCacheItemSizeInMemory(LocatedCompilationError compilationError) =>
            100 + (compilationError.location?.filePath.Sum(e => e.Length) ?? 0) + EstimateCacheItemSizeInMemory(compilationError.error);

        static long EstimateCacheItemSizeInMemory(CompilationError compilationError) =>
            compilationError.DependencyError != null ? compilationError.DependencyError.Length * 2 : 0;

        static long EstimateCacheItemSizeInMemory(CompilerSerialInterface.DependencyKey dependencyKey) =>
            dependencyKey.ElmMakeDependency?.Sum(EstimateCacheItemSizeInMemory) ?? 0;

        static long EstimateCacheItemSizeInMemory(CompilerSerialInterface.ElmMakeRequestStructure elmMakeRequest) =>
            elmMakeRequest?.files?.Sum(file => file.content.AsBase64.Length) ?? 0;

        public record CompilationIterationReport(
            CompilationIterationCompilationReport compilation,
            IReadOnlyList<TimedReport<CompilationIterationDependencyReport>> dependenciesReports,
            int? totalTimeSpentMilli);

        public record CompilationIterationCompilationReport(
            int serializeTimeSpentMilli,
            int? prepareJsEngineTimeSpentMilli,
            int? inJsEngineTimeSpentMilli,
            int? deserializeTimeSpentMilli,
            int totalTimeSpentMilli);

        public record CompilationIterationDependencyReport(string dependencyKeySummary);

        public record TimedReport<T>(T report, int totalTimeSpentMilli);

        public record CompilationError(
            string DependencyError = null,
            string OtherError = null)
        {
            static public CompilationError AsCompilationError(CompilerSerialInterface.CompilationError compilationError)
            {
                var missingDependencyError = compilationError.MissingDependencyError?.FirstOrDefault();

                if (missingDependencyError != null)
                    return new CompilationError(DependencyError: "Missing dependency: " + DescribeCompilationError(compilationError));

                return new CompilationError(OtherError: compilationError.OtherCompilationError?.FirstOrDefault());
            }
        }

        public record LocatedCompilationError(CompilerSerialInterface.LocationInSourceFiles location, CompilationError error);
    }

    namespace CompilerSerialInterface
    {
        public record CompilationError(
            IReadOnlyList<string> OtherCompilationError = null,
            IReadOnlyList<DependencyKey> MissingDependencyError = null);

        record LocatedInSourceFilesRecord(LocationInSourceFiles location);

        record LocatedCompilationError(LocationInSourceFiles location, CompilationError error);

        public record LocationInSourceFiles(IReadOnlyList<string> filePath);

        public record DependencyKey(IReadOnlyList<ElmMakeRequestStructure> ElmMakeDependency);

        public record ElmMakeRequestStructure(
            IReadOnlyList<AppCodeEntry> files,
            IReadOnlyList<string> entryPointFilePath,
            ElmMakeOutputType outputType,
            bool enableDebug);

        public record ElmMakeOutputType(
            IReadOnlyList<object> ElmMakeOutputTypeHtml = null,
            IReadOnlyList<object> ElmMakeOutputTypeJs = null);

        public record AppCodeEntry(IReadOnlyList<string> path, BytesJson content);

        public record BytesJson(string AsBase64)
        {
            static public BytesJson AsJson(IReadOnlyList<byte> bytes) =>
                new(AsBase64: Convert.ToBase64String(bytes as byte[] ?? bytes.ToArray()));
        }
    }
}