﻿using System;
using Avalonia;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.ReactiveUI;

namespace Portramatic
{
    
    // To Publish: 
    // dotnet publish -r win-x64 -c Release -p:PublishReadyToRun=true --self-contained -o c:\tmp\publish -p:PublishSingleFile=true -p:DebugType=embedded -p:IncludeAllContentForSelfExtract=true
    class Program
    {
        // Initialization code. Don't use any Avalonia, third-party APIs or any
        // SynchronizationContext-reliant code before AppMain is called: things aren't initialized
        // yet and stuff might break.
        public static void Main(string[] args) => BuildAvaloniaApp()
            .StartWithClassicDesktopLifetime(args);

        // Avalonia configuration, don't remove; also used by visual designer.
        public static AppBuilder BuildAvaloniaApp()
            => AppBuilder.Configure<App>()
                .UsePlatformDetect()
                .LogToTrace()
                .UseReactiveUI();
    }
}
