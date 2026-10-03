internal readonly record struct WidgetLayout(float TopHeight, float MiddleTop, float MiddleHeight,
    float BottomTop, float BottomHeight, float ScrollMax)
{
    public static WidgetLayout Calculate(float viewport, float top, float bottom, float content, float padding)
    {
        viewport = Math.Max(0, viewport);
        padding = Math.Clamp(padding, 0, viewport / 2);
        top = Math.Clamp(top, 0, viewport - padding * 2);
        bottom = Math.Clamp(bottom, 0, viewport - padding * 2 - top);
        var middle = Math.Max(0, viewport - padding * 2 - top - bottom);
        return new(top, padding + top, middle, viewport - padding - bottom, bottom,
            Math.Max(0, content - middle));
    }
}

internal enum DataHealth { Loading, Current, Stale, Offline }
internal readonly record struct DataFreshness(DataHealth Health, string Text, string Detail)
{
    public static DataFreshness Describe(DateTime fetched, string? error, TimeSpan staleAfter, DateTime now)
    {
        if (error != null)
            return new(DataHealth.Offline, L.T("No connection", "Ingen forbindelse"), error +
                (fetched == DateTime.MinValue ? "" : L.T("\nLast updated ", "\nSidst opdateret ") + fetched.ToString("dd/MM HH:mm")));
        if (fetched == DateTime.MinValue) return new(DataHealth.Loading, L.T("Fetching…", "Henter…"), L.T("Waiting for the first answer", "Venter på første svar"));
        if (now - fetched > staleAfter)
            return new(DataHealth.Stale, L.T("Outdated data", "Forældede data"), L.T("Last updated ", "Sidst opdateret ") + fetched.ToString("dd/MM HH:mm"));
        return new(DataHealth.Current, L.T("Updated ", "Opdateret ") + fetched.ToString("HH:mm"), L.T("Last updated ", "Sidst opdateret ") + fetched.ToString("dd/MM HH:mm"));
    }
}
