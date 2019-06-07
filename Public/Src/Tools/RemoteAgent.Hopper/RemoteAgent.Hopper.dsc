// Copyright (c) Microsoft. All rights reserved.
// Licensed under the MIT license. See LICENSE file in the project root for full license information.

import * as Managed from "Sdk.Managed";

namespace RemoteAgent.Hopper {
    export declare const qualifier: BuildXLSdk.DefaultQualifier;

    @@public
    export const exe = BuildXLSdk.executable({
        assemblyName: "RemoteAgent.Hopper",
        sources: [
            ...globR(d`.`, "*.cs"),
        ],
    });
}
