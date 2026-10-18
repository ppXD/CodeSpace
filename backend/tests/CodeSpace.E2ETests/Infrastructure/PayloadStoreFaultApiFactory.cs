using Autofac;
using CodeSpace.Core.Persistence.Entities;
using CodeSpace.Core.Services.OAuth;
using CodeSpace.Messages.Credentials;
using Npgsql;

namespace CodeSpace.E2ETests.Infrastructure;

/// <summary>
/// The real API host with its credential payload writer wrapped by <see cref="FaultInjectingCredentialPayloadWriter"/>:
/// while <see cref="PayloadStoreFault"/> holds failures, the store of a rotated OAuth pair throws, as an exhausted pool or
/// a dropped connection does after the provider has already rotated it. Registered on this test host only; nothing in
/// production carries the decorator.
/// </summary>
public class PayloadStoreFaultApiFactory : TaskLaunchApiFactory
{
    public PayloadStoreFault StoreFault { get; } = new();

    protected override void ConfigureContractTestServices(ContainerBuilder builder)
    {
        builder.RegisterInstance(StoreFault).AsSelf().SingleInstance();
        builder.RegisterDecorator<FaultInjectingCredentialPayloadWriter, ICredentialPayloadWriter>();
    }
}

/// <summary>How many of the next stores to fail, set by the test between requests.</summary>
public sealed class PayloadStoreFault
{
    private int _remaining;
    private int _stores;

    /// <summary>The next <paramref name="stores"/> stores throw; zero ends it.</summary>
    public void FailNext(int stores) => Volatile.Write(ref _remaining, stores);

    /// <summary>How many times the refresher tried to store, failed or not.</summary>
    public int Stores => Volatile.Read(ref _stores);

    internal bool TakeFailure()
    {
        Interlocked.Increment(ref _stores);

        return Interlocked.Decrement(ref _remaining) >= 0;
    }
}

public sealed class FaultInjectingCredentialPayloadWriter : ICredentialPayloadWriter
{
    private readonly ICredentialPayloadWriter _inner;
    private readonly PayloadStoreFault _fault;

    public FaultInjectingCredentialPayloadWriter(ICredentialPayloadWriter inner, PayloadStoreFault fault)
    {
        _inner = inner;
        _fault = fault;
    }

    public Task<bool> UpdatePayloadAsync(Credential credential, string expectedEncryptedPayload, CredentialPayload newPayload, CancellationToken cancellationToken)
    {
        if (_fault.TakeFailure()) throw new NpgsqlException("simulated: the connection pool has been exhausted / the connection terminated while storing the rotated pair");

        return _inner.UpdatePayloadAsync(credential, expectedEncryptedPayload, newPayload, cancellationToken);
    }
}
