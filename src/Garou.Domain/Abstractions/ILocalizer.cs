namespace Garou.Domain.Abstractions;

public interface ILocalizer
{
    string this[string key] { get; }
}
