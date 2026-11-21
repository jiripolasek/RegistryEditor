// ------------------------------------------------------------
// Copyright (c) Jiri Polasek. All rights reserved.
// ------------------------------------------------------------

using System.IO.Pipes;
using System.Text;
using JPSoftworks.Regedit.Core.Registry;

namespace JPSoftworks.Regedit.Services;

internal sealed class PowerShellRun(string script, IReadOnlyList<RegistryLocation> targets)
{
    private const int ErrorCharacterLimit = 2048;
    private readonly string _pipeName = $"Regedit.PowerShell.{Guid.NewGuid():N}";

    internal string StartupCommand => $$"""
        $regeditPipe = [IO.Pipes.NamedPipeClientStream]::new('.', '{{this._pipeName}}', [IO.Pipes.PipeDirection]::InOut)
        try {
            $regeditPipe.Connect(30000)
            $regeditReader = [IO.BinaryReader]::new($regeditPipe, [Text.Encoding]::UTF8, $true)
            $regeditStatus = [byte]0
            $regeditError = ''
            try {
                & ([ScriptBlock]::Create($regeditReader.ReadString()))
            } catch {
                $regeditStatus = [byte]1
                $regeditError = $_.ToString()
                Write-Error -ErrorRecord $_ -ErrorAction Continue
            } finally {
                $regeditPipe.WriteByte($regeditStatus)
                $regeditBytes = [Text.Encoding]::UTF8.GetBytes(
                    $regeditError.Substring(0, [Math]::Min($regeditError.Length, {{ErrorCharacterLimit}})))
                $regeditPipe.Write($regeditBytes, 0, $regeditBytes.Length)
                $regeditPipe.Flush()
                $regeditReader.Dispose()
            }
        } finally {
            $regeditPipe.Dispose()
        }
        """;

    internal async Task<PowerShellRunResult> DeliverAsync(CancellationToken cancellationToken)
    {
        // Only the new shell receives the script. Registry data stays off the command line and disk.
        await using var pipe = new NamedPipeServerStream(
            this._pipeName, PipeDirection.InOut, 1, PipeTransmissionMode.Byte,
            PipeOptions.Asynchronous | PipeOptions.CurrentUserOnly);
        using (var startup = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken))
        {
            startup.CancelAfter(TimeSpan.FromSeconds(30));
            try
            {
                await pipe.WaitForConnectionAsync(startup.Token).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
            {
                throw new IOException("PowerShell did not connect to receive the reviewed script.");
            }
        }

        using var payload = new MemoryStream();
        using (var writer = new BinaryWriter(payload, Encoding.UTF8, leaveOpen: true))
        {
            writer.Write(script);
        }

        payload.Position = 0;
        await payload.CopyToAsync(pipe, cancellationToken).ConfigureAwait(false);
        var reply = new byte[Encoding.UTF8.GetMaxByteCount(ErrorCharacterLimit) + 2];
        var length = await pipe.ReadAtLeastAsync(reply, reply.Length, false, cancellationToken).ConfigureAwait(false);
        return length switch
        {
            1 when reply[0] == 0 => new PowerShellRunResult(targets, true, null),
            > 0 when length < reply.Length && reply[0] == 1 => new PowerShellRunResult(
                targets, false, Encoding.UTF8.GetString(reply.AsSpan(1, length - 1))),
            _ => throw new IOException("PowerShell ended without returning a script result.")
        };
    }
}

internal sealed record PowerShellRunResult(IReadOnlyList<RegistryLocation> Targets, bool Succeeded, string? Error);
