using System.Linq;

namespace ZeEngine.Networking;

public static class ServerAddress
{
    /// <summary>Parses "host" or "host:port" (default port 7777). IPv6 literals are not supported.</summary>
    public static bool TryParse(string input, out string host, out int port, out string error)
    {
        host = null;
        port = NetworkManager.DefaultPort;
        error = null;

        input = (input ?? string.Empty).Trim();
        if (input.Length == 0)
        {
            error = "Please enter an address.";
            return false;
        }

        int colon = input.IndexOf(':');
        if (colon >= 0 && colon == input.LastIndexOf(':'))
        {
            string portText = input.Substring(colon + 1);
            input = input.Substring(0, colon);

            if (!int.TryParse(portText, out port) || port < 1 || port > 65535)
            {
                error = "Port must be between 1 and 65535.";
                return false;
            }
        }

        if (input.Length == 0 || input.Any(char.IsWhiteSpace))
        {
            error = "That doesn't look like a valid address.";
            return false;
        }

        host = input;
        return true;
    }
}