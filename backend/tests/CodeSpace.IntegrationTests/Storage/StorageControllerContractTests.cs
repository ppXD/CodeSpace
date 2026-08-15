using System.Reflection;
using CodeSpace.Api.Controllers;
using Microsoft.AspNetCore.Http.Metadata;
using Microsoft.AspNetCore.Mvc;
using Shouldly;

namespace CodeSpace.IntegrationTests.Storage;

[Trait("Category", "Integration")]
public sealed class StorageControllerContractTests
{
    [Fact]
    public void Every_storage_mutation_has_the_same_bounded_request_body_contract()
    {
        var mutationMethods = new[]
        {
            nameof(StorageController.CreateProfile), nameof(StorageController.AppendProfileRevision), nameof(StorageController.SetProfileState),
            nameof(StorageController.CreateCredential), nameof(StorageController.AppendCredentialRevision), nameof(StorageController.RevokeCredential),
        };

        foreach (var methodName in mutationMethods)
        {
            var method = typeof(StorageController).GetMethod(methodName, BindingFlags.Instance | BindingFlags.Public).ShouldNotBeNull();
            var limit = method.GetCustomAttribute<RequestSizeLimitAttribute>().ShouldNotBeNull();
            ((IRequestSizeLimitMetadata)limit).MaxRequestBodySize.ShouldBe(StorageController.MaxMutationBodyBytes);
        }
    }
}
