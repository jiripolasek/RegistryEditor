// ------------------------------------------------------------
// Copyright (c) Jiri Polasek. All rights reserved.
// ------------------------------------------------------------

using System.Security.Principal;
using Microsoft.Win32;
using Microsoft.Win32.SafeHandles;

namespace JPSoftworks.Regedit.Core.Registry;

public static class RemoteRegistryCredentials
{
    private static readonly Dictionary<string, Session> Sessions = new(StringComparer.OrdinalIgnoreCase);
    private static readonly AsyncLocal<string?> AuthenticatingComputer = new();

    public static IDisposable Retain(IEnumerable<string?> computers)
    {
        var distinct = computers.OfType<string>().Distinct(StringComparer.OrdinalIgnoreCase).ToArray();
        lock (Sessions)
        {
            return new Connections(distinct.Select(Retain).ToArray());
        }
    }

    public static IDisposable Retain(string computer)
    {
        lock (Sessions)
        {
            if (!Sessions.TryGetValue(computer, out var session))
            {
                session = new Session();
                Sessions.Add(computer, session);
            }

            session.Connections++;
            return new Connection(computer, session);
        }
    }

    internal static int ConnectionCount(string computer)
    {
        lock (Sessions)
        {
            return Sessions.TryGetValue(computer, out var session) ? session.Connections : 0;
        }
    }

    public static async Task AuthenticateAsync(string computer, SafeAccessTokenHandle token, Func<Task> connect)
    {
        using var connection = Retain(computer);
        await WindowsIdentity.RunImpersonatedAsync(
            token, async () =>
            {
                var previous = AuthenticatingComputer.Value;
                AuthenticatingComputer.Value = computer;
                try
                {
                    await connect();
                }
                finally
                {
                    AuthenticatingComputer.Value = previous;
                }
            });

        // Keep only a token after a successful probe; credentials are never written to settings.
        var identity = new WindowsIdentity(token.DangerousGetHandle());
        WindowsIdentity? replaced;
        lock (Sessions)
        {
            var session = Sessions[computer];
            replaced = session.Identity;
            session.Identity = identity;
        }

        replaced?.Dispose();
    }

    public static RegistryKey OpenBaseKey(RegistryHive hive, string computer, RegistryView view)
    {
        return Run(computer, () => RegistryKey.OpenRemoteBaseKey(hive, computer, view));
    }

    internal static T Run<T>(string? computer, Func<T> action)
    {
        if (computer is null || string.Equals(AuthenticatingComputer.Value, computer, StringComparison.OrdinalIgnoreCase))
        {
            return action();
        }

        WindowsIdentity? identity;
        lock (Sessions)
        {
            // Duplicate under the lock so reconnecting cannot dispose a token another worker is about to use.
            identity = Sessions.TryGetValue(computer, out var session) && session.Identity is { } cached
                ? new WindowsIdentity(cached.AccessToken.DangerousGetHandle())
                : null;
        }

        using (identity)
        {
            if (identity is null)
            {
                return action();
            }

            return WindowsIdentity.RunImpersonated(
                identity.AccessToken, () =>
                {
                    var previous = AuthenticatingComputer.Value;
                    AuthenticatingComputer.Value = computer;
                    try
                    {
                        return action();
                    }
                    finally
                    {
                        AuthenticatingComputer.Value = previous;
                    }
                });
        }
    }

    private sealed class Session
    {
        internal int Connections;
        internal WindowsIdentity? Identity;
    }

    private sealed class Connections : IDisposable
    {
        private IDisposable[]? _connections;

        internal Connections(IDisposable[] connections)
        {
            this._connections = connections;
        }

        public void Dispose()
        {
            foreach (var connection in Interlocked.Exchange(ref this._connections, null) ?? [])
            {
                connection.Dispose();
            }
        }
    }

    private sealed class Connection : IDisposable
    {
        private readonly string _computer;
        private Session? _session;

        internal Connection(string computer, Session session)
        {
            this._computer = computer;
            this._session = session;
        }

        public void Dispose()
        {
            var owned = Interlocked.Exchange(ref this._session, null);
            if (owned is null)
            {
                return;
            }

            WindowsIdentity? identity = null;
            lock (Sessions)
            {
                if (--owned.Connections == 0)
                {
                    Sessions.Remove(this._computer);
                    identity = owned.Identity;
                    owned.Identity = null;
                }
            }

            identity?.Dispose();
        }
    }
}
