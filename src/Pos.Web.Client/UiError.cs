using Microsoft.JSInterop;

namespace Pos.Web.Client;

internal static class UiError
{
    public static string Message(Exception exception)
    {
        if (exception is not JSException)
        {
            return exception.Message;
        }
        string firstLine = exception.Message.Split('\n', 2)[0].Trim();
        return firstLine.StartsWith("Error: ", StringComparison.Ordinal)
            ? firstLine[7..]
            : firstLine;
    }
}
