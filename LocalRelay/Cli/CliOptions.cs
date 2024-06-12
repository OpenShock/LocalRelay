using CommandLine;

namespace OpenShock.LocalRelay.Cli;

public class CliOptions
{
    [Option("headless", Required = false, Default = false, HelpText = "Run the application in headless mode.")]
    public required bool Headless { get; init; }
}