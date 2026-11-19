namespace RuijieCampusAuth.App.ViewModels;

public sealed record ProfileOption(
    string Path,
    string Name,
    string AuthMode,
    string ServiceName)
{
    public string Display => $"{Name} ({AuthMode})";
}
