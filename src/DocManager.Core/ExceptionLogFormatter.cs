namespace DocManager.Core;

public static class ExceptionLogFormatter
{
    public static string Format(string context, Exception exception)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(context);
        ArgumentNullException.ThrowIfNull(exception);
        return $"{context.Trim()}: {exception.ToString()}";
    }
}
