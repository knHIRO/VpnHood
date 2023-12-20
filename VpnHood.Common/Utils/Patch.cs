namespace VpnHood.Common.Utils;

public class Patch<T>
{
    public T Value { get; }

    public Patch(T value)
    {
        Value = value;
    }

    public override int GetHashCode()
    {
        return Value?.GetHashCode() ?? "".GetHashCode();
    }

    public override string ToString()
    {
        return Value?.ToString() ?? "";
    }

    public override bool Equals(object? obj)
    {
        return Equals(Value, obj);
    }

    public static implicit operator Patch<T>(T value)
    {
        return new Patch<T>(value);
    }

    public static implicit operator T(Patch<T> value)
    {
        return value != null ? value.Value : throw new NullReferenceException("Value has not been set.");
    }
}