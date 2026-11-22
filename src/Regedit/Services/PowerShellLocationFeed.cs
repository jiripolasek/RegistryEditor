// ------------------------------------------------------------
// Copyright (c) Jiri Polasek. All rights reserved.
// ------------------------------------------------------------

#if REGEDIT_EXPERIMENTAL_TERMINAL
using System.IO.Pipes;
using System.Runtime.CompilerServices;
using System.Text;
using JPSoftworks.Regedit.Core.Registry;
using Microsoft.Win32;

namespace JPSoftworks.Regedit.Services;

internal sealed class PowerShellLocationFeed(RegistryView view)
{
    private readonly string _pipeName = $"Regedit.PowerShell.Location.{Guid.NewGuid():N}";
    private NamedPipeServerStream? _requestPipe;
    private TaskCompletionSource<string?>? _pendingRequest;

    internal string StartupCommand => $$"""
        if ($null -ne $ExecutionContext.InvokeCommand.PSObject.Properties['LocationChangedAction']) {
            $regeditLocationPipe = $null
            try {
                $regeditLocationPipe = [IO.Pipes.NamedPipeClientStream]::new(
                    '.', '{{this._pipeName}}', [IO.Pipes.PipeDirection]::InOut,
                    [IO.Pipes.PipeOptions]'Asynchronous, CurrentUserOnly')
                $regeditLocationPipe.Connect(500)
                $regeditSendLocation = {
                    param($sender, $change)
                    $location = $change.NewPath
                    $message = if ($location.Provider.Name -eq 'Registry') { 'R' + $location.ProviderPath } else { '-' }
                    $bytes = [Text.Encoding]::UTF8.GetBytes($message)
                    try {
                        $regeditLocationPipe.Write($bytes, 0, $bytes.Length)
                    } catch [IO.IOException] {
                        <# Regedit can close the receiver while PowerShell is exiting. #>
                    }
                }.GetNewClosure()
                & $regeditSendLocation $null @{ NewPath = $ExecutionContext.SessionState.Path.CurrentLocation }
                $ExecutionContext.InvokeCommand.LocationChangedAction = $regeditSendLocation

                $regeditLocationReader = [IO.StreamReader]::new($regeditLocationPipe)
                $global:regeditLocationState = @{
                    Pipe = $regeditLocationPipe
                    Reader = $regeditLocationReader
                    Read = $regeditLocationReader.ReadLineAsync()
                    Ready = $false
                }
                $null = Register-EngineEvent -SourceIdentifier PowerShell.OnIdle -SupportEvent -Action {
                    <# PSReadLine's idle event does not carry message data. #>
                    $state = $global:regeditLocationState
                    if (($state.Ready -and !$state.Read.IsCompleted) -or
                        $null -eq ('Microsoft.PowerShell.PSConsoleReadLine' -as [type])) { return }
                    $line = ''
                    $cursor = 0
                    [Microsoft.PowerShell.PSConsoleReadLine]::GetBufferState([ref]$line, [ref]$cursor)
                    if ($line.Length -ne 0) { return }

                    try {
                        if (!$state.Ready) {
                            $state.Pipe.WriteByte([byte][char]'+')
                            $state.Ready = $true
                        }
                        if (!$state.Read.IsCompleted) { return }
                        $request = $state.Read.GetAwaiter().GetResult()
                        if ($null -eq $request) {
                            Unregister-Event -SubscriptionId $eventSubscriber.SubscriptionId -Force
                            $state.Reader.Dispose()
                            return
                        }
                        $state.Read = $state.Reader.ReadLineAsync()
                        try {
                            $key = [Text.Encoding]::UTF8.GetString([Convert]::FromBase64String($request))
                            Microsoft.PowerShell.Management\Set-Location -LiteralPath $key -ErrorAction Stop
                            [Microsoft.PowerShell.PSConsoleReadLine]::InvokePrompt()
                            $state.Pipe.WriteByte([byte][char]'A')
                        } catch {
                            $problem = $_.Exception.Message
                            $reply = [Text.Encoding]::UTF8.GetBytes('E' + $problem.Substring(0, [Math]::Min($problem.Length, 1024)))
                            $state.Pipe.Write($reply, 0, $reply.Length)
                        }
                    } catch [IO.IOException] {
                        Unregister-Event -SubscriptionId $eventSubscriber.SubscriptionId -Force
                        $state.Reader.Dispose()
                    }
                }
            } catch {
                if ($null -ne $regeditLocationPipe) { $regeditLocationPipe.Dispose() }
            }
        }
        """;

    internal bool IsLocationChangePending => Volatile.Read(ref this._pendingRequest) is not null;

    internal bool CanSetLocation(RegistryLocation? location) =>
        location is { Machine: null, IsComputer: false } && location.View == view &&
        Volatile.Read(ref this._requestPipe) is not null && !this.IsLocationChangePending;

    internal async Task<string?> SetLocationAsync(RegistryLocation location, CancellationToken cancellationToken)
    {
        if (!this.CanSetLocation(location) || Volatile.Read(ref this._requestPipe) is not { } pipe)
        {
            return "Select a local key in the same registry view as the active PowerShell tab.";
        }

        var response = new TaskCompletionSource<string?>(TaskCreationOptions.RunContinuationsAsynchronously);
        if (Interlocked.CompareExchange(ref this._pendingRequest, response, null) is not null)
        {
            return "PowerShell is waiting to apply a location change.";
        }

        try
        {
            var path = location.FormatPath(RegistryPathFormat.PowerShellProvider)!;
            var message = Convert.ToBase64String(Encoding.UTF8.GetBytes(path)) + '\n';
            await pipe.WriteAsync(Encoding.ASCII.GetBytes(message), cancellationToken).ConfigureAwait(false);
            return await response.Task.WaitAsync(cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            Interlocked.CompareExchange(ref this._pendingRequest, null, response);
        }
    }

    internal async IAsyncEnumerable<RegistryLocation?> WatchAsync(
        [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        await using var pipe = new NamedPipeServerStream(
            this._pipeName, PipeDirection.InOut, 1, PipeTransmissionMode.Message,
            PipeOptions.Asynchronous | PipeOptions.CurrentUserOnly);
        await pipe.WaitForConnectionAsync(cancellationToken).ConfigureAwait(false);

        var buffer = new byte[64 * 1024];
        RegistryLocation? currentLocation = null;
        try
        {
            int count;
            while ((count = await pipe.ReadAsync(buffer, cancellationToken).ConfigureAwait(false)) > 0)
            {
                if (!pipe.IsMessageComplete)
                {
                    yield break;
                }

                switch (buffer[0])
                {
                    case (byte)'+':
                        Volatile.Write(ref this._requestPipe, pipe);
                        break;
                    case (byte)'A':
                        Volatile.Read(ref this._pendingRequest)?.TrySetResult(null);
                        continue;
                    case (byte)'E':
                        Volatile.Read(ref this._pendingRequest)?.TrySetResult(Encoding.UTF8.GetString(buffer, 1, count - 1));
                        continue;
                    case (byte)'R':
                        currentLocation = this.ParseLocation(Encoding.UTF8.GetString(buffer, 1, count - 1));
                        break;
                    case (byte)'-':
                        currentLocation = null;
                        break;
                    default:
                        continue;
                }

                yield return currentLocation;
            }
        }
        finally
        {
            Volatile.Write(ref this._requestPipe, null);
            Volatile.Read(ref this._pendingRequest)?.TrySetResult("The PowerShell session ended.");
        }
    }

    private RegistryLocation? ParseLocation(string path)
    {
        try
        {
            var location = RegistryLocation.Parse(path, view);
            return location is { Machine: null, IsComputer: false } ? location : null;
        }
        catch (ArgumentException)
        {
            return null;
        }
    }
}
#endif
