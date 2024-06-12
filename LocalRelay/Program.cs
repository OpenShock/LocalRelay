// using OpenShock.LocalRelay;
// using OpenShock.LocalRelay.Models.Serial;
// using OpenShock.SDK.CSharp.Models;
// using Serilog;
//
// Log.Logger = new LoggerConfiguration()
//     .MinimumLevel.Verbose()
//     .WriteTo.Console()
//     .CreateLogger();
//     
// var builder = Host.CreateDefaultBuilder();
// builder.UseSerilog(Log.Logger);
// builder.ConfigureServices(services =>
// {
//     
// });
//
// var app = builder.Build();
//
// var serialPortClient = new SerialPortClient(app.Services.GetRequiredService<ILogger<SerialPortClient>>(), "COM13");
// await serialPortClient.Open();
//
// var deviceConnection = new DeviceConnection(new Uri("https://api.shocklink.net"),
//     "",
//     app.Services.GetRequiredService<ILogger<DeviceConnection>>());
//
// deviceConnection.OnControlMessage += async commands =>
// {
//     foreach (var cmd in commands.Commands)
//     {
//         await serialPortClient.Control(new RfTransmit
//         {
//             Id = cmd.Id,
//             Intensity = cmd.Intensity,
//             Model = (ShockerModelType)cmd.Model,
//             Type = (ControlType)cmd.Type,
//             DurationMs = cmd.Duration
//         });
//     }
// };
//
// await deviceConnection.InitializeAsync();
//
// await app.RunAsync();