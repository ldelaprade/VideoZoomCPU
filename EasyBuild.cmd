 :: Create a single-file, self-contained executable for Windows x64
 dotnet  publish -c Release -r win-x64 /p:AllowUnsafeBlocks=true --self-contained true /p:PublishSingleFile=true /p:IncludeAllContentForSelfExtract=true
