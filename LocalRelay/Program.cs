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
