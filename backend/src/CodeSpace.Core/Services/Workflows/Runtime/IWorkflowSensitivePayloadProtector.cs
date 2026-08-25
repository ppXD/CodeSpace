namespace CodeSpace.Core.Services.Workflows.Runtime;

public interface IWorkflowSensitivePayloadProtector
{
    string Protect(string plaintext);
    string Unprotect(string ciphertext);
}
