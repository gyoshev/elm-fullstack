module Backend.VolatileProcess exposing (programCode)


programCode : String
programCode =
    """
#r "netstandard"
#r "System"
#r "System.Collections.Immutable"
#r "System.Net"
#r "System.Net.Http"
#r "System.Net.Primitives"
#r "System.Private.Uri"
#r "System.Linq"

/*
Use the `elm-fs  describe` command to get the hash of a file:

elm-fs  describe  ./assembly/assembly.dll
Loaded composition d0adb9c0632d35c7a982da279276879c7d49a99dd7c004277f78d34dd718e961 from './assembly/assembly.dll'.
Composition d0adb9c0632d35c7a982da279276879c7d49a99dd7c004277f78d34dd718e961 is a blob containing 4096 bytes
*/
#r "sha256:d0adb9c0632d35c7a982da279276879c7d49a99dd7c004277f78d34dd718e961"

using System;
using System.Collections.Generic;
using System.Collections.Immutable;
using System.Linq;
using System.Net.Http;


string InterfaceToHost_Request(string request)
{
    /*
    Use names as found using ILSpy in d0adb9c0632d35c7a982da279276879c7d49a99dd7c004277f78d34dd718e961.
    */
    return assembly.Class.Function(request);
}

"""
