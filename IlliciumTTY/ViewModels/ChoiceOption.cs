namespace IlliciumTTY.ViewModels;

public sealed record ChoiceOption<T>(string Label, T Value)
{
    public override string ToString() => Label;
}

public sealed record FolderChoiceOption(string Label, ConnectionNodeViewModel? Node)
{
    public override string ToString() => Label;
}