// Copyright (c) Microsoft.  All Rights Reserved.  Licensed under the Apache License, Version 2.0.  See License.txt in the project root for license information.

using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading;
using System.Web;
using System.Windows.Forms;
using Microsoft.NodejsTools.Debugger;
using Microsoft.NodejsTools.Debugger.DebugEngine;
using Microsoft.NodejsTools.TypeScript;
using Microsoft.VisualStudio;
using Microsoft.VisualStudio.Shell;
using Microsoft.VisualStudio.Shell.Interop;
using Microsoft.VisualStudioTools.Project;

namespace Microsoft.NodejsTools.Project
{
    internal class NodejsProjectLauncher : IProjectLauncher
    {
        private readonly NodejsProjectNode _project;
        private int? _testServerPort;

        private static readonly Guid WebkitDebuggerGuid = Guid.Parse("4cc6df14-0ab5-4a91-8bb4-eb0bf233d0fe");
        private static readonly Guid WebkitPortSupplierGuid = Guid.Parse("4103f338-2255-40c0-acf5-7380e2bea13d");

        public NodejsProjectLauncher(NodejsProjectNode project)
        {
            this._project = project;

            var portNumber = this._project.GetProjectProperty(NodeProjectProperty.NodejsPort);
            int portNum;
            if (Int32.TryParse(portNumber, out portNum))
            {
                this._testServerPort = portNum;
            }
        }

        #region IProjectLauncher Members

        public int LaunchProject(bool debug)
        {
            NodejsPackage.Instance.Logger.LogEvent(Logging.NodejsToolsLogEvent.Launch, debug ? 1 : 0);
            return Start(ResolveStartupFile(), debug);
        }

        public int LaunchFile(string file, bool debug)
        {
            NodejsPackage.Instance.Logger.LogEvent(Logging.NodejsToolsLogEvent.Launch, debug ? 1 : 0);
            return Start(file, debug);
        }

        private int Start(string file, bool debug)
        {
            var nodePath = GetNodePath();

            if (nodePath == null)
            {
                Nodejs.ShowNodejsNotInstalled();
                return VSConstants.S_OK;
            }

            var chromeProtocolRequired = Nodejs.GetNodeVersion(nodePath) >= new Version(8, 0);
            var startBrowser = ShouldStartBrowser();

            if (debug && !chromeProtocolRequired)
            {
                StartWithDebugger(file);
            }
            else if (debug && chromeProtocolRequired)
            {
                StartAndAttachDebugger(file, nodePath);
            }
            else
            {
                StartNodeProcess(file, nodePath, startBrowser);
            }

            return VSConstants.S_OK;
        }

        private void StartAndAttachDebugger(string file, string nodePath)
        {
            // start the node process
            var workingDir = _project.GetWorkingDirectory();
            var url = GetFullUrl();
            var env = GetEnvironmentVariablesString(url);
            var interpreterOptions = _project.GetProjectProperty(NodeProjectProperty.NodeExeArguments);
            var debugOptions = this.GetDebugOptions();
            var script = GetFullArguments(file, includeNodeArgs: false);

            var process = NodeDebugger.StartNodeProcessWithInspect(exe: nodePath, script: script, dir: workingDir, env: env, interpreterOptions: interpreterOptions, debugOptions: debugOptions);
            process.Start();

            // setup debug info and attach
            var debugUri = $"http://127.0.0.1:{process.DebuggerPort}";

            var dbgInfo = new VsDebugTargetInfo4();
            dbgInfo.dlo = (uint)DEBUG_LAUNCH_OPERATION.DLO_AlreadyRunning;
            dbgInfo.LaunchFlags = (uint)__VSDBGLAUNCHFLAGS.DBGLAUNCH_StopDebuggingOnEnd;

            dbgInfo.guidLaunchDebugEngine = WebkitDebuggerGuid;
            dbgInfo.dwDebugEngineCount = 1;

            var enginesPtr = MarshalDebugEngines(new[] { WebkitDebuggerGuid });
            dbgInfo.pDebugEngines = enginesPtr;
            dbgInfo.guidPortSupplier = WebkitPortSupplierGuid;
            dbgInfo.bstrPortName = debugUri;
            dbgInfo.fSendToOutputWindow = 0;

            // we connect through a URI, so no need to set the process,
            // we need to set the process id to '1' so the debugger is able to attach
            dbgInfo.bstrExe = $"\01";

            AttachDebugger(dbgInfo);
        }

        private NodeDebugOptions GetDebugOptions()
        {
            var debugOptions = NodeDebugOptions.None;

            if (NodejsPackage.Instance.GeneralOptionsPage.WaitOnAbnormalExit)
            {
                debugOptions |= NodeDebugOptions.WaitOnAbnormalExit;
            }

            if (NodejsPackage.Instance.GeneralOptionsPage.WaitOnNormalExit)
            {
                debugOptions |= NodeDebugOptions.WaitOnNormalExit;
            }

            return debugOptions;
        }

        private void AttachDebugger(VsDebugTargetInfo4 dbgInfo)
        {
            var serviceProvider = _project.Site;

            var debugger = serviceProvider.GetService(typeof(SVsShellDebugger)) as IVsDebugger4;

            if (debugger == null)
            {
                throw new InvalidOperationException("Failed to get the debugger service.");
            }

            var launchResults = new VsDebugTargetProcessInfo[1];
            debugger.LaunchDebugTargets4(1, new[] { dbgInfo }, launchResults);
        }

        private static IntPtr MarshalDebugEngines(Guid[] debugEngines)
        {
            if (debugEngines.Length == 0)
            {
                return IntPtr.Zero;
            }

            var guidSize = Marshal.SizeOf(typeof(Guid));
            var size = debugEngines.Length * guidSize;
            var bytes = new byte[size];
            for (var i = 0; i < debugEngines.Length; ++i)
            {
                debugEngines[i].ToByteArray().CopyTo(bytes, i * guidSize);
            }

            var pDebugEngines = Marshal.AllocCoTaskMem(size);
            Marshal.Copy(bytes, 0, pDebugEngines, size);

            return pDebugEngines;
        }

        private void StartNodeProcess(string file, string nodePath, bool startBrowser)
        {
            //TODO: looks like this duplicates a bunch of code in NodeDebugger
            var psi = new ProcessStartInfo()
            {
                UseShellExecute = false,

                FileName = nodePath,
                Arguments = GetFullArguments(file, includeNodeArgs: true),
                WorkingDirectory = _project.GetWorkingDirectory()
            };

            var webBrowserUrl = GetFullUrl();
            Uri uri = null;
            if (!String.IsNullOrWhiteSpace(webBrowserUrl))
            {
                uri = new Uri(webBrowserUrl);
                psi.EnvironmentVariables["PORT"] = uri.Port.ToString();
            }

            foreach (var nameValue in GetEnvironmentVariables())
            {
                psi.EnvironmentVariables[nameValue.Key] = nameValue.Value;
            }

            var process = NodeProcess.Start(
                psi,
                waitOnAbnormal: NodejsPackage.Instance.GeneralOptionsPage.WaitOnAbnormalExit,
                waitOnNormal: NodejsPackage.Instance.GeneralOptionsPage.WaitOnNormalExit);

            this._project.OnDispose += process.ResponseToTerminateEvent;

            if (startBrowser && uri != null)
            {
                OnPortOpenedHandler.CreateHandler(
                    uri.Port,
                    shortCircuitPredicate: () => process.HasExited,
                    action: () =>
                    {
                        VsShellUtilities.OpenBrowser(webBrowserUrl, (uint)__VSOSPFLAGS.OSP_LaunchNewBrowser);
                    }
                );
            }
        }

        private string GetFullArguments(string file, bool includeNodeArgs)
        {
            var res = string.Empty;
            if (includeNodeArgs)
            {
                var nodeArgs = this._project.GetProjectProperty(NodeProjectProperty.NodeExeArguments);
                if (!string.IsNullOrWhiteSpace(nodeArgs))
                {
                    res = nodeArgs + " ";
                }
            }

            res += "\"" + file + "\"";
            var scriptArgs = this._project.GetProjectProperty(NodeProjectProperty.ScriptArguments);
            if (!string.IsNullOrWhiteSpace(scriptArgs))
            {
                res += " " + scriptArgs;
            }
            return res;
        }

        private string GetNodePath()
        {
            var overridePath = this._project.GetProjectProperty(NodeProjectProperty.NodeExePath);
            return Nodejs.GetAbsoluteNodeExePath(this._project.ProjectHome, overridePath);
        }

        #endregion

        private string GetFullUrl()
        {
            var host = this._project.GetProjectProperty(NodeProjectProperty.LaunchUrl);

            try
            {
                return GetFullUrl(host, this.TestServerPort);
            }
            catch (UriFormatException)
            {
                var output = OutputWindowRedirector.GetGeneral(NodejsPackage.Instance);
                output.WriteErrorLine(string.Format(CultureInfo.CurrentCulture, Resources.ErrorInvalidLaunchUrl, host));
                output.ShowAndActivate();
                return string.Empty;
            }
        }

        internal static string GetFullUrl(string host, int port)
        {
            UriBuilder builder;
            Uri uri;
            if (Uri.TryCreate(host, UriKind.Absolute, out uri))
            {
                builder = new UriBuilder(uri);
            }
            else
            {
                builder = new UriBuilder();
                builder.Scheme = Uri.UriSchemeHttp;
                builder.Host = "localhost";
                builder.Path = host;
            }

            builder.Port = port;

            return builder.ToString();
        }

        private int TestServerPort
        {
            get
            {
                if (!this._testServerPort.HasValue)
                {
                    this._testServerPort = GetFreePort();
                }
                return this._testServerPort.Value;
            }
        }

        /// <summary>
        /// Default implementation of the "Start Debugging" command.
        /// </summary>
        private void StartWithDebugger(string startupFile)
        {
            var dbgInfo = new VsDebugTargetInfo();
            dbgInfo.cbSize = (uint)Marshal.SizeOf(dbgInfo);

            if (SetupDebugInfo(ref dbgInfo, startupFile))
            {
                LaunchDebugger(this._project.Site, dbgInfo);
            }
        }

        private void LaunchDebugger(IServiceProvider provider, VsDebugTargetInfo dbgInfo)
        {
            if (!Directory.Exists(dbgInfo.bstrCurDir))
            {
                MessageBox.Show(string.Format(CultureInfo.CurrentCulture, Resources.DebugWorkingDirectoryDoesNotExistErrorMessage, dbgInfo.bstrCurDir), SR.ProductName);
            }
            else if (!File.Exists(dbgInfo.bstrExe))
            {
                MessageBox.Show(string.Format(CultureInfo.CurrentCulture, Resources.DebugInterpreterDoesNotExistErrorMessage, dbgInfo.bstrExe), SR.ProductName);
            }
            else if (DoesProjectSupportDebugging())
            {
                VsShellUtilities.LaunchDebugger(provider, dbgInfo);
            }
        }

        private bool DoesProjectSupportDebugging()
        {
            var typeScriptOutFile = this._project.GetProjectProperty("TypeScriptOutFile");
            if (!string.IsNullOrEmpty(typeScriptOutFile))
            {
                return MessageBox.Show(
                    Resources.DebugTypeScriptCombineNotSupportedWarningMessage,
                    SR.ProductName,
                    MessageBoxButtons.YesNo,
                    MessageBoxIcon.Warning
                    ) == DialogResult.Yes;
            }

            return true;
        }

        private void AppendOption(ref VsDebugTargetInfo dbgInfo, string option, string value)
        {
            if (!string.IsNullOrWhiteSpace(dbgInfo.bstrOptions))
            {
                dbgInfo.bstrOptions += ";";
            }

            dbgInfo.bstrOptions += option + "=" + HttpUtility.UrlEncode(value);
        }

        /// <summary>
        /// Sets up debugger information.
        /// </summary>
        private bool SetupDebugInfo(ref VsDebugTargetInfo dbgInfo, string startupFile)
        {
            dbgInfo.dlo = DEBUG_LAUNCH_OPERATION.DLO_CreateProcess;

            dbgInfo.bstrExe = GetNodePath();
            dbgInfo.bstrCurDir = this._project.GetWorkingDirectory();
            dbgInfo.bstrArg = GetFullArguments(startupFile, includeNodeArgs: false);    // we need to supply node args via options
            dbgInfo.bstrRemoteMachine = null;
            var nodeArgs = this._project.GetProjectProperty(NodeProjectProperty.NodeExeArguments);
            if (!string.IsNullOrWhiteSpace(nodeArgs))
            {
                AppendOption(ref dbgInfo, AD7Engine.InterpreterOptions, nodeArgs);
            }

            var url = GetFullUrl();
            if (ShouldStartBrowser() && !string.IsNullOrWhiteSpace(url))
            {
                AppendOption(ref dbgInfo, AD7Engine.WebBrowserUrl, url);
            }

            var debuggerPort = this._project.GetProjectProperty(NodeProjectProperty.DebuggerPort);
            if (!string.IsNullOrWhiteSpace(debuggerPort))
            {
                AppendOption(ref dbgInfo, AD7Engine.DebuggerPort, debuggerPort);
            }

            if (NodejsPackage.Instance.GeneralOptionsPage.WaitOnAbnormalExit)
            {
                AppendOption(ref dbgInfo, AD7Engine.WaitOnAbnormalExitSetting, "true");
            }

            if (NodejsPackage.Instance.GeneralOptionsPage.WaitOnNormalExit)
            {
                AppendOption(ref dbgInfo, AD7Engine.WaitOnNormalExitSetting, "true");
            }

            dbgInfo.fSendStdoutToOutputWindow = 0;
            dbgInfo.bstrEnv = GetEnvironmentVariablesString(url);


            // Set the Node  debugger
            dbgInfo.clsidCustom = AD7Engine.DebugEngineGuid;
            dbgInfo.grfLaunch = (uint)__VSDBGLAUNCHFLAGS.DBGLAUNCH_StopDebuggingOnEnd;
            return true;
        }

        private string GetEnvironmentVariablesString(string url)
        {
            var env = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            if (!string.IsNullOrWhiteSpace(url))
            {
                var webUrl = new Uri(url);
                env["PORT"] = webUrl.Port.ToString();
            }

            foreach (var nameValue in GetEnvironmentVariables())
            {
                env[nameValue.Key] = nameValue.Value;
            }

            if (env.Count > 0)
            {
                // add any inherited env vars
                var variables = Environment.GetEnvironmentVariables();
                foreach (var key in variables.Keys)
                {
                    var strKey = (string)key;
                    if (!env.ContainsKey(strKey))
                    {
                        env.Add(strKey, (string)variables[key]);
                    }
                }

                //Environment variables should be passed as a
                //null-terminated block of null-terminated strings. 
                //Each string is in the following form:name=value\0
                var buf = new StringBuilder();
                foreach (var entry in env)
                {
                    buf.AppendFormat("{0}={1}\0", entry.Key, entry.Value);
                }
                buf.Append("\0");
                return buf.ToString();
            }

            return null;
        }

        private bool ShouldStartBrowser()
        {
            var startBrowser = this._project.GetProjectProperty(NodeProjectProperty.StartWebBrowser);
            bool fStartBrowser;
            if (!string.IsNullOrEmpty(startBrowser) &&
                Boolean.TryParse(startBrowser, out fStartBrowser))
            {
                return fStartBrowser;
            }

            return true;
        }

        private IEnumerable<KeyValuePair<string, string>> GetEnvironmentVariables()
        {
            var envVars = this._project.GetProjectProperty(NodeProjectProperty.Environment);
            if (envVars != null)
            {
                foreach (var envVar in envVars.Split(new[] { '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries))
                {
                    var nameValue = envVar.Split(new[] { '=' }, 2);
                    if (nameValue.Length == 2)
                    {
                        yield return new KeyValuePair<string, string>(nameValue[0], nameValue[1]);
                    }
                }
            }
        }

        private static int GetFreePort()
        {
            return Enumerable.Range(new Random().Next(1200, 2000), 60000).Except(
                from connection in IPGlobalProperties.GetIPGlobalProperties().GetActiveTcpConnections()
                select connection.LocalEndPoint.Port
            ).First();
        }

        private string ResolveStartupFile()
        {
            var startupFile = this._project.GetStartupFile();
            if (string.IsNullOrEmpty(startupFile))
            {
                throw new ApplicationException(Resources.DebugCouldNotResolveStartupFileErrorMessage);
            }

            if (TypeScriptHelpers.IsTypeScriptFile(startupFile))
            {
                startupFile = TypeScriptHelpers.GetTypeScriptBackedJavaScriptFile(this._project, startupFile);
            }
            return startupFile;
        }
    }

    internal class OnPortOpenedHandler
    {
        private class OnPortOpenedInfo
        {
            public readonly int Port;
            public readonly TimeSpan? Timeout;
            public readonly int Sleep;
            public readonly Func<bool> ShortCircuitPredicate;
            public readonly Action Action;
            public readonly DateTime StartTime;

            public OnPortOpenedInfo(
                int port,
                int? timeout = null,
                int? sleep = null,
                Func<bool> shortCircuitPredicate = null,
                Action action = null
            )
            {
                this.Port = port;
                if (timeout.HasValue)
                {
                    this.Timeout = TimeSpan.FromMilliseconds(Convert.ToDouble(timeout));
                }
                this.Sleep = sleep ?? 500;                                   // 1/2 second sleep
                this.ShortCircuitPredicate = shortCircuitPredicate ?? (() => false);
                this.Action = action ?? (() => { });
                this.StartTime = System.DateTime.Now;
            }
        }

        internal static void CreateHandler(
            int port,
            int? timeout = null,
            int? sleep = null,
            Func<bool> shortCircuitPredicate = null,
            Action action = null
        )
        {
            ThreadPool.QueueUserWorkItem(
                OnPortOpened,
                new OnPortOpenedInfo(
                    port,
                    timeout,
                    sleep,
                    shortCircuitPredicate,
                    action
                )
            );
        }

        private static void OnPortOpened(object infoObj)
        {
            var info = (OnPortOpenedInfo)infoObj;

            using (var socket = new Socket(AddressFamily.InterNetwork, SocketType.Stream, ProtocolType.Tcp))
            {
                socket.Blocking = true;
                try
                {
                    while (true)
                    {
                        // Short circuit
                        if (info.ShortCircuitPredicate())
                        {
                            return;
                        }

                        // Try connect
                        try
                        {
                            socket.Connect(IPAddress.Loopback, info.Port);
                            break;
                        }
                        catch
                        {
                            // Connect failure
                            // Fall through
                        }

                        // Timeout
                        if (info.Timeout.HasValue && (System.DateTime.Now - info.StartTime) >= info.Timeout)
                        {
                            break;
                        }

                        // Sleep
                        System.Threading.Thread.Sleep(info.Sleep);
                    }
                }
                finally
                {
                    socket.Close();
                }
            }

            // Launch browser (if not short-circuited)
            if (!info.ShortCircuitPredicate())
            {
                info.Action();
            }
        }
    }
}

