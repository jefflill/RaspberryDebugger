﻿//-----------------------------------------------------------------------------
// FILE:	    ProjectPropertiesPanel.cs
// CONTRIBUTOR: Jeff Lill
// COPYRIGHT:   Open Source
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

using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Drawing;
using System.Data;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using System.Windows.Forms;

using Microsoft.VisualStudio.Threading;

using Neon.Common;

namespace RaspberryDebug
{
    /// <summary>
    /// Implements the custom Raspberry project debug properties page. 
    /// </summary>
    public partial class ProjectPropertiesPanel : UserControl
    {
        /// <summary>
        /// Constructor.
        /// </summary>
        public ProjectPropertiesPanel()
        {
            InitializeComponent();
        }
    }
}
