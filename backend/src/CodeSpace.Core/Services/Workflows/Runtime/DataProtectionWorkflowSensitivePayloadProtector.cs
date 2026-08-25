using CodeSpace.Core.DependencyInjection;
using Microsoft.AspNetCore.DataProtection;

namespace CodeSpace.Core.Services.Workflows.Runtime;

public sealed class DataProtectionWorkflowSensitivePayloadProtector : IWorkflowSensitivePayloadProtector, ISingletonDependency
{
    internal const string ProtectorPurpose = "CodeSpace.Workflows.SensitiveRecordPayload.v1";

    private readonly IDataProtector _protector;

    public DataProtectionWorkflowSensitivePayloadProtector(IDataProtectionProvider provider) => _protector = provider.CreateProtector(ProtectorPurpose);

    public string Protect(string plaintext) => _protector.Protect(plaintext);
    public string Unprotect(string ciphertext) => _protector.Unprotect(ciphertext);
}
