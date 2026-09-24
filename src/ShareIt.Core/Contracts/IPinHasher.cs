namespace ShareIt.Core.Contracts;

public interface IPinHasher
{
    string Hash(string pin);
    bool Verify(string pin, string hash);
}
