namespace O2P.Application.Interfaces
{
    public interface ISecretProtector
    {
        byte[] Protect(string plaintext);
        string Unprotect(byte[] protectedBytes);
        bool IsProtected(byte[]? value);
    }
}
