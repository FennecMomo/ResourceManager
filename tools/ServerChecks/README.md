# Invisible server connection checks

Run `dotnet run --project tools/ServerChecks/ServerChecks.csproj -c Release` from the repository root on Windows.

Uses fresh data below `dist/server-checks`, two ephemeral loopback servers, and an unshown WPF window. Checks independent tabs, roster push, reconnect after server restart, cached offline state, replaced session behavior, and safe shutdown. Writes offscreen layout images at 1260 and 1010 pixels; no desktop screenshot or input injection.

0.6.2 adds publication target selection, synchronized resource trees/counts, selection/details at two window sizes, server update discovery, and persistence of offline publisher catalogs.
