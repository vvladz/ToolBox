#!/usr/bin/env dotnet
#:property TargetFramework=net11.0
#:property LangVersion=15.0
#:property PublishAot=false
#:property Nullable=enable
#:property TreatWarningsAsErrors=true
#:include ../lib/Contracts.cs
#:include ../lib/Manifest.cs
#:include ../lib/RunLog.cs
#:include ../lib/ProcessRunner.cs
#:include ../lib/Cli.cs
#:include ../lib/Installer.cs
#:include ../lib/Git.cs
#:include ../etc/Commands.cs

return await Toolbox.Cli.RunAsync(args, Toolbox.Commands.Create());
