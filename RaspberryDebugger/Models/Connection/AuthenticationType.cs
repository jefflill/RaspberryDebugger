﻿//-----------------------------------------------------------------------------
// FILE:	    AuthenticationType.cs
// CONTRIBUTOR: Jeff Lill
// COPYRIGHT:   Copyright (c) 2024 by neonFORGE, LLC.  All rights reserved.
//
// Licensed under the Apache License, Version 2.0 (the "License");
// you may not use this file except in compliance with the License.
// You may obtain a copy of the License at
//
//     http://www.apache.org/licenses/LICENSE-2.0
//
// Unless required by applicable law or agreed to in writing, software
// distributed under the License is distributed on an "AS IS" BASIS,
// WITHOUT WARRANTIES OR CONDITIONS OF ANY KIND, either express or implied.
// See the License for the specific language governing permissions and
// limitations under the License.

using System.Runtime.Serialization;

namespace RaspberryDebugger.Models.Connection
{
    /// <summary>
    /// Enumerates the supported SSH authentication types.
    /// </summary>
    internal enum AuthenticationType
    {
        /// <summary>
        /// Password based authentication.
        /// </summary>
        [EnumMember(Value = "password")]
        PASSWORD = 0,

        /// <summary>
        /// Public SSH key based authentication.
        /// </summary>
        [EnumMember(Value = "public-key")]
        PUBLIC_KEY
    }
}
