using System.Text;

namespace AdbBridge.Core.Relay;

/// <summary>
/// Parses the adb "smart socket" host protocol out of one relayed connection's byte
/// stream, purely to detect <c>forward:</c>/<c>killforward</c> requests — the mechanism
/// behind Android Studio's Device Mirroring and App Inspection tools, which are invisible
/// to a plain byte-for-byte relay (see <see cref="TcpRelay"/> and <see cref="ForwardPortRelay"/>
/// for why that matters).
///
/// Every adb host request is framed as a 4-byte lowercase-hex length followed by that
/// many bytes of an ASCII service string (e.g. "000dhost:version"); a one-shot host:
/// command like "forward:" replies with a service status followed by an operation
/// "OKAY"/"FAIL", and — only when the client asked
/// for an OS-assigned port via "tcp:0" — a second 4-hex-length-prefixed frame carrying the
/// resolved port as a decimal string. This only ever buffers a few dozen bytes: as soon as
/// a connection's request frame is parsed and it isn't a forward-family command, watching
/// stops for good and the relay's raw pass-through is untouched for the rest of that
/// connection (a `shell:` or `sync:` session's actual payload is never buffered here).
/// </summary>
public sealed class AdbForwardWatcher
{
    private const int MaxServiceStringLength = 4096;
    private const int MaxResponseBuffer = 64;

    private enum RequestState { AwaitingLength, AwaitingBody, Done }
    private enum ResponseState { Idle, AwaitingStatus, AwaitingCommandStatus, AwaitingResolvedLength, AwaitingResolvedBody, Done }
    private enum ForwardKind { None, Install, Remove, RemoveAll, ReverseInstall, ReverseRemove, ReverseRemoveAll }

    private readonly object _gate = new();

    private RequestState _reqState = RequestState.AwaitingLength;
    private readonly List<byte> _reqBuf = new();
    private int _reqBodyLength = -1;
    private bool _transportSelectHandled;

    private ResponseState _respState = ResponseState.Idle;
    private readonly List<byte> _respBuf = new();
    private int _respResolvedLength = -1;

    private ForwardKind _kind = ForwardKind.None;
    private int _port = -1; // -1 = unknown, 0 = "resolve from response", >0 = known local port
    private string? _reverseRemoteSpec;

    public event EventHandler<int>? ForwardEstablished;
    public event EventHandler<int>? ForwardRemoved;
    public event EventHandler? ForwardsCleared;

    /// <summary>
    /// adb reverse is the mirror image of adb forward: instead of a new Host-side port
    /// the Companion needs to tunnel a listener for, the CLIENT (Studio) opens the local
    /// listener itself and asks the real adb server to deliver device-originated
    /// connections to it. See <see cref="ReversePoolRelay"/> for why that needs a pooled
    /// tunnel rather than a plain listener. RemoteSpec is the device-side half (e.g.
    /// "localabstract:screen-sharing-agent-51553") — adb's killforward for reverse
    /// mappings identifies the mapping by this, not by the local port.
    /// </summary>
    public event EventHandler<(string RemoteSpec, int LocalPort)>? ReverseForwardEstablished;
    public event EventHandler<string>? ReverseForwardRemoved;
    public event EventHandler? ReverseForwardsCleared;

    /// <summary>Diagnostic: fires for every adb service string this connection requests
    /// (both the transport-select and, when present, the per-device service that follows
    /// it), regardless of whether it's forward-related.</summary>
    public event EventHandler<string>? ServiceObserved;

    /// <summary>Feed bytes flowing from the adb client toward the server (the request side).</summary>
    public void OnClientToServer(ReadOnlySpan<byte> data)
    {
        lock (_gate)
        {
            if (_reqState == RequestState.Done) return;
            AppendRequest(data);
        }
    }

    /// <summary>Feed bytes flowing from the adb server back to the client (the response side).</summary>
    public void OnServerToClient(ReadOnlySpan<byte> data)
    {
        lock (_gate)
        {
            if (_respState is ResponseState.Idle or ResponseState.Done) return;
            AppendResponse(data);
        }
    }

    private void AppendRequest(ReadOnlySpan<byte> data)
    {
        _reqBuf.AddRange(data.ToArray());
        if (_reqBuf.Count > 4 + MaxServiceStringLength)
        {
            _reqState = RequestState.Done;
            return;
        }

        if (_reqState == RequestState.AwaitingLength)
        {
            if (_reqBuf.Count < 4) return;

            if (!RelayPump.TryParseHexLength(_reqBuf[0], _reqBuf[1], _reqBuf[2], _reqBuf[3], out _reqBodyLength) ||
                _reqBodyLength > MaxServiceStringLength)
            {
                _reqState = RequestState.Done;
                return;
            }
            _reqState = RequestState.AwaitingBody;
        }

        if (_reqState == RequestState.AwaitingBody)
        {
            if (_reqBuf.Count < 4 + _reqBodyLength) return;

            var service = Encoding.ASCII.GetString(_reqBuf.GetRange(4, _reqBodyLength).ToArray());
            ServiceObserved?.Invoke(this, service);

            // A "select this device" request (host:transport:<serial>, host:transport-id:<id>,
            // host-serial:<serial>:transport, ...) is followed by a SECOND framed request on
            // the same connection — the actual per-device service being invoked (shell:,
            // sync:, jdwp:<pid>, localabstract:<name>, ...). Diagnostic-only: reset to parse
            // that second frame too, purely so ServiceObserved reports it; forward detection
            // below is unaffected since forward:/killforward requests never go through a
            // transport-select first.
            if (!_transportSelectHandled && (service.Contains("transport", StringComparison.Ordinal) ||
                service.StartsWith("host:tport:", StringComparison.Ordinal)))
            {
                _transportSelectHandled = true;
                _reqBuf.RemoveRange(0, 4 + _reqBodyLength);
                _reqState = RequestState.AwaitingLength;
                if (_reqBuf.Count > 0) AppendRequest(ReadOnlySpan<byte>.Empty);
                return;
            }

            _reqState = RequestState.Done;
            ClassifyService(service);
        }
    }

    private void ClassifyService(string service)
    {
        if (TryClassifyReverse(service))
        {
            _respState = ResponseState.AwaitingStatus;
            return;
        }

        if (!service.StartsWith("host", StringComparison.Ordinal))
            return;

        const string killAllMarker = ":killforward-all";
        const string killMarker = ":killforward:";
        const string fwdMarker = ":forward:";

        if (service.EndsWith(killAllMarker, StringComparison.Ordinal))
        {
            _kind = ForwardKind.RemoveAll;
        }
        else
        {
            var killIdx = service.IndexOf(killMarker, StringComparison.Ordinal);
            if (killIdx >= 0)
            {
                var localSpec = service[(killIdx + killMarker.Length)..];
                if (!TryParseTcpPort(localSpec, out var port) || port == 0) return;
                _kind = ForwardKind.Remove;
                _port = port;
            }
            else
            {
                var fwdIdx = service.IndexOf(fwdMarker, StringComparison.Ordinal);
                if (fwdIdx < 0) return;

                var rest = service[(fwdIdx + fwdMarker.Length)..];
                if (rest.StartsWith("norebind:", StringComparison.Ordinal))
                    rest = rest["norebind:".Length..];

                var semi = rest.IndexOf(';');
                var localSpec = semi >= 0 ? rest[..semi] : rest;
                if (!TryParseTcpPort(localSpec, out var port)) return;

                _kind = ForwardKind.Install;
                _port = port;
            }
        }

        _respState = ResponseState.AwaitingStatus;
    }

    /// <summary>
    /// adb reverse requests are per-device (sent after a transport-select, like shell:/
    /// sync:), never "host"-prefixed, so this is checked before the host: family above.
    /// killforward for a reverse mapping names the device-side remote spec, not a port —
    /// there's no local port to parse out of it at all.
    /// </summary>
    private bool TryClassifyReverse(string service)
    {
        const string killAllMarker = "reverse:killforward-all";
        const string killMarker = "reverse:killforward:";
        const string fwdMarker = "reverse:forward:";

        if (service.Equals(killAllMarker, StringComparison.Ordinal))
        {
            _kind = ForwardKind.ReverseRemoveAll;
            return true;
        }

        if (service.StartsWith(killMarker, StringComparison.Ordinal))
        {
            _kind = ForwardKind.ReverseRemove;
            _reverseRemoteSpec = service[killMarker.Length..];
            return true;
        }

        if (service.StartsWith(fwdMarker, StringComparison.Ordinal))
        {
            var rest = service[fwdMarker.Length..];
            if (rest.StartsWith("norebind:", StringComparison.Ordinal))
                rest = rest["norebind:".Length..];

            var semi = rest.IndexOf(';');
            if (semi < 0) return false;

            var remoteSpec = rest[..semi];
            var localSpec = rest[(semi + 1)..];
            // Unlike host:forward:, Studio already owns the local listener before asking
            // for the reverse mapping, so it always specifies an explicit, non-zero port.
            if (!TryParseTcpPort(localSpec, out var port) || port <= 0) return false;

            _kind = ForwardKind.ReverseInstall;
            _reverseRemoteSpec = remoteSpec;
            _port = port;
            return true;
        }

        return false;
    }

    private void AppendResponse(ReadOnlySpan<byte> data)
    {
        _respBuf.AddRange(data.ToArray());
        if (_respBuf.Count > MaxResponseBuffer)
        {
            _respState = ResponseState.Done;
            return;
        }

        if (_respState == ResponseState.AwaitingStatus)
        {
            if (_respBuf.Count < 4) return;

            var status = Encoding.ASCII.GetString(_respBuf.GetRange(0, 4).ToArray());
            _respBuf.RemoveRange(0, 4);

            if (!string.Equals(status, "OKAY", StringComparison.Ordinal))
            {
                _respState = ResponseState.Done; // FAIL, or garbage — nothing to relay
                return;
            }

            // The smart-socket service acceptance and the forwarding operation each
            // return a status. The first OKAY does not mean the mapping succeeded.
            _respState = ResponseState.AwaitingCommandStatus;
        }

        if (_respState == ResponseState.AwaitingCommandStatus)
        {
            if (_respBuf.Count < 4) return;
            var status = Encoding.ASCII.GetString(_respBuf.GetRange(0, 4).ToArray());
            _respBuf.RemoveRange(0, 4);
            if (status != "OKAY")
            {
                _respState = ResponseState.Done;
                return;
            }

            if (_kind == ForwardKind.Install && _port == 0)
            {
                _respState = ResponseState.AwaitingResolvedLength;
            }
            else
            {
                Fire();
                _respState = ResponseState.Done;
                return;
            }
        }

        if (_respState == ResponseState.AwaitingResolvedLength)
        {
            if (_respBuf.Count < 4) return;

            if (!RelayPump.TryParseHexLength(_respBuf[0], _respBuf[1], _respBuf[2], _respBuf[3], out _respResolvedLength) ||
                _respResolvedLength is <= 0 or > 6)
            {
                _respState = ResponseState.Done; // no resolved-port payload arrived — give up quietly
                return;
            }
            _respBuf.RemoveRange(0, 4);
            _respState = ResponseState.AwaitingResolvedBody;
        }

        if (_respState == ResponseState.AwaitingResolvedBody)
        {
            if (_respBuf.Count < _respResolvedLength) return;

            var text = Encoding.ASCII.GetString(_respBuf.GetRange(0, _respResolvedLength).ToArray());
            if (int.TryParse(text, out var resolvedPort) && resolvedPort is > 0 and <= 65535)
            {
                _port = resolvedPort;
                Fire();
            }
            _respState = ResponseState.Done;
        }
    }

    private void Fire()
    {
        switch (_kind)
        {
            case ForwardKind.Install when _port > 0:
                ForwardEstablished?.Invoke(this, _port);
                break;
            case ForwardKind.Remove when _port > 0:
                ForwardRemoved?.Invoke(this, _port);
                break;
            case ForwardKind.RemoveAll:
                ForwardsCleared?.Invoke(this, EventArgs.Empty);
                break;
            case ForwardKind.ReverseInstall when _port > 0 && _reverseRemoteSpec is not null:
                ReverseForwardEstablished?.Invoke(this, (_reverseRemoteSpec, _port));
                break;
            case ForwardKind.ReverseRemove when _reverseRemoteSpec is not null:
                ReverseForwardRemoved?.Invoke(this, _reverseRemoteSpec);
                break;
            case ForwardKind.ReverseRemoveAll:
                ReverseForwardsCleared?.Invoke(this, EventArgs.Empty);
                break;
        }
    }

    private static bool TryParseTcpPort(string spec, out int port)
    {
        port = -1;
        const string prefix = "tcp:";
        if (!spec.StartsWith(prefix, StringComparison.Ordinal)) return false;
        return int.TryParse(spec[prefix.Length..], out port) && port is >= 0 and <= 65535;
    }

}
