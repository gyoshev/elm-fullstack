using System;
using System.Collections.Generic;
using System.Collections.Immutable;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.RegularExpressions;
using JavaScriptEngineSwitcher.ChakraCore;
using JavaScriptEngineSwitcher.Core;
using Newtonsoft.Json;

namespace Kalmit
{
    public interface IProcess<EventT, ResponseT>
    {
        ResponseT ProcessEvent(EventT serializedEvent);

        string GetSerializedState();

        string SetSerializedState(string serializedState);
    }

    public interface IProcessWithStringInterface : IProcess<string, string>
    {
    }

    public interface IDisposableProcessWithStringInterface : IProcessWithStringInterface, IDisposable
    {
    }

    public class ProcessHostedWithChakraCore : IDisposableProcessWithStringInterface
    {
        readonly IJsEngine javascriptEngine;

        static public int? OverrideChakraCoreSettingsMaxStackSize = null;

        static public JsEngineBase ConstructJsEngine()
        {
            return new ChakraCoreJsEngine(
                new ChakraCoreSettings
                {
                    DisableEval = true,
                    EnableExperimentalFeatures = true,
                    MaxStackSize = OverrideChakraCoreSettingsMaxStackSize ?? 10_000_000,
                }
            );
        }

        public ProcessHostedWithChakraCore(string javascriptPreparedToRun)
        {
            javascriptEngine = ConstructJsEngine();

            var initAppResult = javascriptEngine.Evaluate(javascriptPreparedToRun);

            var resetAppStateResult = javascriptEngine.Evaluate(
                ProcessFromElm019Code.appStateJsVarName + " = " + ProcessFromElm019Code.initStateJsFunctionPublishedSymbol + ";");
        }

        static string AsJavascriptExpression(string originalString) =>
            JsonConvert.SerializeObject(originalString);

        public void Dispose()
        {
            javascriptEngine?.Dispose();
        }

        public string ProcessEvent(string serializedEvent)
        {
            /*
            Avoid high memory usage as described in exploration 2020-02-02:
            Use specialized implementation based on `CallFunction` instead of `Evaluate`.

            var eventExpression = AsJavascriptExpression(serializedEvent);

            var expressionJavascript = ProcessFromElm019Code.processEventSyncronousJsFunctionName + "(" + eventExpression + ")";

            return EvaluateInJsEngineAndReturnResultAsString(expressionJavascript);
            */

            var evalResult = javascriptEngine.CallFunction(
                ProcessFromElm019Code.processEventSyncronousJsFunctionName, serializedEvent);

            return evalResult?.ToString();
        }

        public string GetSerializedState()
        {
            var expressionJavascript =
                ProcessFromElm019Code.serializeStateJsFunctionPublishedSymbol +
                "(" + ProcessFromElm019Code.appStateJsVarName + ")";

            return EvaluateInJsEngineAndReturnResultAsString(expressionJavascript);
        }

        public string SetSerializedState(string serializedState)
        {
            var serializedStateExpression = AsJavascriptExpression(serializedState);

            var expressionJavascript =
                ProcessFromElm019Code.appStateJsVarName +
                " = " + ProcessFromElm019Code.deserializeStateJsFunctionPublishedSymbol +
                "(" + serializedStateExpression + ");";

            return EvaluateInJsEngineAndReturnResultAsString(expressionJavascript);
        }

        string EvaluateInJsEngineAndReturnResultAsString(string expressionJavascript)
        {
            var evalResult = javascriptEngine.Evaluate(expressionJavascript);

            return evalResult?.ToString();
        }
    }

    public class ProcessFromElm019Code
    {
        static public (IDisposableProcessWithStringInterface process,
            (string javascriptFromElmMake, string javascriptPreparedToRun) buildArtifacts)
            ProcessFromElmCodeFiles(
            IReadOnlyCollection<(IImmutableList<string>, IImmutableList<byte>)> elmCodeFiles,
            ElmAppInterfaceConfig? overrideElmAppInterfaceConfig = null) =>
            ProcessFromElmCodeFiles(ElmApp.ToFlatDictionaryWithPathComparer(elmCodeFiles), overrideElmAppInterfaceConfig);

        static public (IDisposableProcessWithStringInterface process,
            (string javascriptFromElmMake, string javascriptPreparedToRun) buildArtifacts)
            ProcessFromElmCodeFiles(
            IImmutableDictionary<IImmutableList<string>, IImmutableList<byte>> elmCodeFiles,
            ElmAppInterfaceConfig? overrideElmAppInterfaceConfig = null)
        {
            var elmAppInterfaceConfig = overrideElmAppInterfaceConfig ?? ElmAppInterfaceConfig.Default;

            var javascriptFromElmMake = CompileElmToJavascript(
                elmCodeFiles,
                ElmApp.FilePathFromModuleName(ElmApp.InterfaceToHostRootModuleName));

            var pathToFunctionCommonStart = ElmApp.InterfaceToHostRootModuleName + ".";

            var javascriptPreparedToRun =
                BuildAppJavascript(
                    javascriptFromElmMake,
                    pathToFunctionCommonStart + ElmAppInterfaceConvention.ProcessSerializedEventFunctionName,
                    pathToFunctionCommonStart + ElmAppInterfaceConvention.InitialStateFunctionName,
                    pathToFunctionCommonStart + ElmAppInterfaceConvention.SerializeStateFunctionName,
                    pathToFunctionCommonStart + ElmAppInterfaceConvention.DeserializeStateFunctionName);

            return
                (new ProcessHostedWithChakraCore(javascriptPreparedToRun),
                (javascriptFromElmMake, javascriptPreparedToRun));
        }

        static public string CompileElmToJavascript(
            IImmutableDictionary<IImmutableList<string>, IImmutableList<byte>> elmCodeFiles,
            IImmutableList<string> pathToFileWithElmEntryPoint,
            string elmMakeCommandAppendix = null) =>
            CompileElm(elmCodeFiles, pathToFileWithElmEntryPoint, "file-for-elm-make-output.js", elmMakeCommandAppendix);

        static public string CompileElmToHtml(
            IImmutableDictionary<IImmutableList<string>, IImmutableList<byte>> elmCodeFiles,
            IImmutableList<string> pathToFileWithElmEntryPoint,
            string elmMakeCommandAppendix = null) =>
            CompileElm(elmCodeFiles, pathToFileWithElmEntryPoint, "file-for-elm-make-output.html", elmMakeCommandAppendix);

        /*
        2019-12-14: Switch to modeling file paths as a list of string instead of a string, to avoid that problem reported earlier and described below:

        Unify directory separator symbols in file names to avoid this problem observed 2019-07-31:
        I had built web-app-config.zip on a Windows system. Starting the webserver with this worked as expected in Windows. But in a Docker container it failed, with an error as below:
        ----
        Output file not found. Maybe the output from the Elm make process helps to find the cause:
        Exit Code: 1
        Standard Output:
        ''
        Standard Error:
        '-- BAD JSON ----------------------------------------------------------- elm.json

        The "source-directories" in your elm.json lists the following directory:

            src

        I cannot find that directory though! Is it missing? Is there a typo?
        [...]
        */
        static string CompileElm(
            IImmutableDictionary<IImmutableList<string>, IImmutableList<byte>> elmCodeFiles,
            IImmutableList<string> pathToFileWithElmEntryPoint,
            string outputFileName,
            string elmMakeCommandAppendix = null)
        {
            /*
            2020-04-01: Avoid the sporadic failures as reported at
            https://github.com/elm-fullstack/elm-fullstack/blob/a206b8095e9f2300f413ef381342db1dca790542/explore/2020-04-01.automate-testing/2020-04-01.automate-testing.md
            Retry for these class of errors.
            */
            var maxRetryCount = 2;

            var command = "make " + makePlatformSpecificPath(pathToFileWithElmEntryPoint) + " --output=\"" + outputFileName + "\" " + elmMakeCommandAppendix;

            var attemptsResults = new List<(ExecutableFile.ProcessOutput processOutput, IReadOnlyCollection<(string name, IImmutableList<byte> content)> resultingFiles)>();

            do
            {
                var commandResults = ExecutableFile.ExecuteFileWithArguments(
                    elmCodeFiles.Select(elmCodeFile => (makePlatformSpecificPath(elmCodeFile.Key), elmCodeFile.Value)).ToImmutableList(),
                    GetElmExecutableFile,
                    command,
                    new Dictionary<string, string>()
                    {
                    //  Avoid elm make failing on `getAppUserDataDirectory`.
                    /* Also, work around problems with elm make like this:
                    -- HTTP PROBLEM ----------------------------------------------------------------

                    The following HTTP request failed:
                        <https://github.com/elm/core/zipball/1.0.0/>

                    Here is the error message I was able to extract:

                    HttpExceptionRequest Request { host = "github.com" port = 443 secure = True
                    requestHeaders = [("User-Agent","elm/0.19.0"),("Accept-Encoding","gzip")]
                    path = "/elm/core/zipball/1.0.0/" queryString = "" method = "GET" proxy =
                    Nothing rawBody = False redirectCount = 10 responseTimeout =
                    ResponseTimeoutDefault requestVersion = HTTP/1.1 } (StatusCodeException
                    (Response {responseStatus = Status {statusCode = 429, statusMessage = "Too
                    Many Requests"}, responseVersion = HTTP/1.1, responseHeaders =
                    [("Server","GitHub.com"),("Date","Sun, 18 Nov 2018 16:53:18
                    GMT"),("Content-Type","text/html"),("Transfer-Encoding","chunked"),("Status","429
                    Too Many
                    Requests"),("Retry-After","120")

                    To avoid elm make failing with this error, break isolation here and reuse elm home directory.
                    An alternative would be retrying when this error is parsed from `commandResults.processOutput.StandardError`.
                    */
                    {"ELM_HOME", GetElmHomeDirectory()},
                    });

                attemptsResults.Add(commandResults);

                var outputFileContent =
                    commandResults.resultingFiles.FirstOrDefault(resultFile => resultFile.name == outputFileName).content;

                if (outputFileContent != null)
                    return Encoding.UTF8.GetString(outputFileContent.ToArray());

                var errorQualifiesForRetry =
                    commandResults.processOutput.StandardError?.Contains("openBinaryFile: resource busy (file is locked)") ?? false;

                if (!errorQualifiesForRetry)
                    break;

            } while (attemptsResults.Count <= maxRetryCount);

            var lastAttemptResults = attemptsResults.Last();

            throw new NotImplementedException(
                "Failed for " + attemptsResults.Count.ToString() + " attempts. Output file not found. Maybe the output from the Elm make process from the last attempt helps to find the cause:" +
                "\nExit Code: " + lastAttemptResults.processOutput.ExitCode +
                "\nStandard Output:\n'" + lastAttemptResults.processOutput.StandardOutput + "'" +
                "\nStandard Error:\n'" + lastAttemptResults.processOutput.StandardError + "'");
        }

        static string makePlatformSpecificPath(IImmutableList<string> path) =>
            string.Join(Path.DirectorySeparatorChar.ToString(), path);

        static byte[] GetElmExecutableFile =>
        CommonConversion.DecompressGzip(GetElmExecutableFileCompressedGzip);

        static byte[] GetElmExecutableFileCompressedGzip =>
        BlobLibrary.GetBlobWithSHA256(CommonConversion.ByteArrayFromStringBase16(
            System.Runtime.InteropServices.RuntimeInformation.IsOSPlatform(OSPlatform.Linux)
            ?
            /*
            Loaded 2019-10-29 from
            https://github.com/elm/compiler/releases/download/0.19.1/binary-for-linux-64-bit.gz
            */
            "e44af52bb27f725a973478e589d990a6428e115fe1bb14f03833134d6c0f155c"
            :
            /*
            Loaded 2019-10-29 from
            https://github.com/elm/compiler/releases/download/0.19.1/binary-for-windows-64-bit.gz
            */
            "d1bf666298cbe3c5447b9ca0ea608552d750e5d232f9845c2af11907b654903b"));

        public const string appStateJsVarName = "app_state";

        public const string initStateJsFunctionPublishedSymbol = "initState";

        public const string serializedEventFunctionPublishedSymbol = "serializedEvent";

        public const string serializeStateJsFunctionPublishedSymbol = "serializeState";

        public const string deserializeStateJsFunctionPublishedSymbol = "deserializeState";

        /*
        Takes the javascript as emitted from Elm make 0.19 and inserts additional statements to
        prepare the script for usage in our application.
        This preparation includes:
        + Publish interfacing functions of app to the global scope.
        + Add a function which implements processing an event and storing the resulting process state and returns the response of the process.
        */
        static string BuildAppJavascript(
            string javascriptFromElmMake,
            string pathToSerializedEventFunction,
            string pathToInitialStateFunction,
            string pathToSerializeStateFunction,
            string pathToDeserializeStateFunction)
        {
            var javascriptMinusCrashes = JavascriptMinusCrashes(javascriptFromElmMake);

            var listFunctionToPublish =
                new[]
                {
                    (functionNameInElm: pathToSerializedEventFunction,
                    publicName: serializedEventFunctionPublishedSymbol,
                    arity: 2),

                    (functionNameInElm: pathToInitialStateFunction,
                    publicName: initStateJsFunctionPublishedSymbol,
                    arity: 0),

                    (functionNameInElm: pathToSerializeStateFunction,
                    publicName: serializeStateJsFunctionPublishedSymbol,
                    arity: 1),

                    (functionNameInElm: pathToDeserializeStateFunction,
                    publicName: deserializeStateJsFunctionPublishedSymbol,
                    arity: 1),
                };

            var processEventAndUpdateStateFunctionJavascriptLines = new[]
            {
                "var " + processEventSyncronousJsFunctionName + " = function(eventSerial){",
                "var newStateAndResponse = " + serializedEventFunctionPublishedSymbol + "(eventSerial," + appStateJsVarName + ");",
                appStateJsVarName + " = newStateAndResponse.a;",
                "return newStateAndResponse.b;",
                "}",
            };

            var processEventAndUpdateStateFunctionJavascript =
                String.Join(Environment.NewLine, processEventAndUpdateStateFunctionJavascriptLines);

            return
                PublishFunctionsFromJavascriptFromElmMake(
                    javascriptMinusCrashes,
                    listFunctionToPublish) +
                Environment.NewLine +
                processEventAndUpdateStateFunctionJavascript;
        }

        static public string PublishFunctionsFromJavascriptFromElmMake(
            string javascriptFromElmMake,
            IEnumerable<(string functionNameInElm, string publicName, int arity)> functions)
        {
            var invokeExportStatementMatch =
                Regex.Matches(javascriptFromElmMake, Regex.Escape("_Platform_export(")).OfType<Match>().LastOrDefault();

            var listFunctionToPublish =
                functions
                .Select(
                    functionToPublish =>
                    new
                    {
                        publicName = functionToPublish.publicName,
                        expression =
                            BuildElmFunctionPublicationExpression(
                                appFunctionSymbolMap(functionToPublish.functionNameInElm), functionToPublish.arity)
                    })
                .ToList();

            var publishStatements =
                listFunctionToPublish
                .Select(functionToPublish => "scope['" + functionToPublish.publicName + "'] = " + functionToPublish.expression + ";")
                .ToList();

            var publicationInsertLocation = invokeExportStatementMatch.Index;

            var publicationInsertString =
                string.Join(Environment.NewLine, new[] { "" }.Concat(publishStatements).Concat(new[] { "" }));

            return
                javascriptFromElmMake.Insert(publicationInsertLocation, publicationInsertString);
        }

        /*
        Work around runtime exception with javascript from Elm make:
        > "Script threw an exception. 'console' is not defined"

        2018-12-02 Observed problematic statements in output from elm make, causing running the script to fail:
        console.warn('Compiled in DEV mode. Follow the advice at https://elm-lang.org/0.19.0/optimize for better performance and smaller assets.');
        [...]
        console.log(tag + ': ' + _Debug_toString(value));

        For some applications collecting the arguments to those functions might be interesting,
        to implement this, have a look at https://github.com/Microsoft/ChakraCore/wiki/JavaScript-Runtime-(JSRT)-Overview
        */
        static public string JavascriptMinusCrashes(string javascriptFromElmMake) =>
            Regex.Replace(
                javascriptFromElmMake,
                "^\\s*console\\.\\w+\\(.+$", "",
                RegexOptions.Multiline);

        public const string processEventSyncronousJsFunctionName = "processEventAndUpdateState";

        static string BuildElmFunctionPublicationExpression(string functionToCallName, int arity)
        {
            if (arity < 2)
                return functionToCallName;

            var paramNameList = Enumerable.Range(0, arity).Select(paramIndex => "param_" + paramIndex).ToList();

            return
                "(" + String.Join(",", paramNameList) + ") => " + functionToCallName +
                String.Join("", paramNameList.Select(paramName => "(" + paramName + ")"));
        }

        static string appFunctionSymbolMap(string pathToFileWithElmEntryPoint) =>
            "$author$project$" + pathToFileWithElmEntryPoint.Replace(".", "$");

        static public string overrideElmMakeHomeDirectory = null;

        static string elmHomeDirectory;

        static string GetElmHomeDirectory()
        {
            elmHomeDirectory =
                overrideElmMakeHomeDirectory ??
                elmHomeDirectory ??
                Path.Combine(Filesystem.CreateRandomDirectoryInTempDirectory(), "elm-home");

            Directory.CreateDirectory(elmHomeDirectory);
            return elmHomeDirectory;
        }
    }
}
