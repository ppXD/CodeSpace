namespace CodeSpace.Core.Services.Credentials;

public interface IPayloadEncryptor
{
    string Encrypt(string plaintext);
    string Decrypt(string ciphertext);
}
