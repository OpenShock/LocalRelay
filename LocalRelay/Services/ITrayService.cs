﻿namespace OpenShock.LocalRelay.Services;

public interface ITrayService
{
    /// <summary>
    /// Setup the tray icon and make it visible
    /// </summary>
    public void Initialize();
}