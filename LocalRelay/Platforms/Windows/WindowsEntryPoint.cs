﻿#if WINDOWS
using System.IO.Pipes;
using System.Runtime.InteropServices;
using System.Text.Json;
using Microsoft.UI.Dispatching;
using OpenShock.LocalRelay.Cli;
using OpenShock.LocalRelay.Cli.Uri;
using OpenShock.LocalRelay.Services.Pipes;
using OpenShock.LocalRelay.Utils;
using WinRT;
using Application = Microsoft.UI.Xaml.Application;
using UriParser = OpenShock.LocalRelay.Cli.Uri.UriParser;

namespace OpenShock.LocalRelay.Platforms.Windows;

public static class WindowsEntryPoint
{
    private const int ATTACH_PARENT_PROCESS = -1;

    [DefaultDllImportSearchPaths(DllImportSearchPath.SafeDirectories)]
    [DllImport("Microsoft.ui.xaml.dll")]
    private static extern void XamlCheckProcessRequirements();

    [DllImport("kernel32.dll")]
    private static extern bool AllocConsole();

    [DllImport("kernel32.dll")]
    private static extern bool AttachConsole(int pid);

    [STAThread]
    private static void Main(string[] args)
    {
        ParseHelper.Parse<MauiCliOptions>(args, Start);
    }

    private static void Start(MauiCliOptions config)
    {
        if (config.Console)
        {
            // Command line given, display console
            if (!AttachConsole(ATTACH_PARENT_PROCESS))
                AllocConsole();
        }

        const string pipeName = @"\\.\pipe\OpenShock.LocalRelay";

        // TODO: Refactor this
        if (PipeHelper.EnumeratePipes().Any(x => x.Equals(pipeName, StringComparison.InvariantCultureIgnoreCase)))
        {
            using var pipeClientStream = new NamedPipeClientStream(".", "OpenShock.LocalRelay", PipeDirection.Out);
            pipeClientStream.Connect(500);

            var parsedUri = UriParser.Parse(config.Uri);

            using var writer = new StreamWriter(pipeClientStream);
            writer.AutoFlush = true;

            if (!string.IsNullOrEmpty(config.Uri))
            {
                var pipeMessage = parsedUri.Type switch
                {
                    UriParameterType.Show => new PipeMessage { Type = PipeMessageType.Show },
                    UriParameterType.Token => new PipeMessage
                    {
                        Type = PipeMessageType.Token, Data = string.Join('/', parsedUri.Arguments)
                    },
                    _ => null
                };

                if (pipeMessage != null) writer.WriteLine(JsonSerializer.Serialize(pipeMessage));

                return;
            }
            
            // Send show message
            writer.WriteLine(JsonSerializer.Serialize(new PipeMessage { Type = PipeMessageType.Show }));

            Console.WriteLine("Another instance of ShockOSC is already running.");
            Environment.Exit(1);
            return;
        }

        if (config.Headless)
        {
            Console.WriteLine("Running in headless mode.");

            var host = HeadlessProgram.SetupHeadlessHost();
            //OsTask.Run(host.Services.GetRequiredService<AuthService>().Authenticate);
            host.Run();

            return;
        }

        XamlCheckProcessRequirements();
        ComWrappersSupport.InitializeComWrappers();
        Application.Start(delegate
        {
            var context = new DispatcherQueueSynchronizationContext(DispatcherQueue.GetForCurrentThread());
            SynchronizationContext.SetSynchronizationContext(context);
            // ReSharper disable once ObjectCreationAsStatement
            new App();
        });
    }
}
#endif