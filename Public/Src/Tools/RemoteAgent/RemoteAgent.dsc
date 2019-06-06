// Copyright (c) Microsoft. All rights reserved.
// Licensed under the MIT license. See LICENSE file in the project root for full license information.

import * as Managed from "Sdk.Managed";

namespace RemoteAgent {

    export declare const qualifier: BuildXLSdk.DefaultQualifier;

    @@public
    export const exe = BuildXLSdk.executable({
        assemblyName: "RemoteAgent",
        sources: [
            ...globR(d`.`, "*.cs"),
            ...importFrom("BuildXL.Engine").Scheduler.protoFiles,
        ],
        references: [
            importFrom("BuildXL.Cache.ContentStore").Hashing.dll,
            importFrom("BuildXL.Cache.ContentStore").Interfaces.dll,
            importFrom("BuildXL.Cache.ContentStore").Library.dll,
            importFrom("BuildXL.Cache.ContentStore").UtilitiesCore.dll,
            importFrom("BuildXL.Engine").Processes.dll,
            importFrom("BuildXL.Pips").dll,
            importFrom("BuildXL.Pips").Proto.dll,
            importFrom("BuildXL.Utilities").dll,
            importFrom("BuildXL.Utilities").Collections.dll,
            importFrom("BuildXL.Utilities").Configuration.dll,
            importFrom("BuildXL.Utilities").Native.dll,
            importFrom("BuildXL.Utilities").Storage.dll,
            importFrom("Google.Protobuf").pkg,
            importFrom("Grpc.Core").pkg,
            importFrom("System.Interactive.Async").pkg,
        ],
    });
}
