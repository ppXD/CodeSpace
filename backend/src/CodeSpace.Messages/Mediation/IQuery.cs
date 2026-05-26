using MediatR;

namespace CodeSpace.Messages.Mediation;

public interface IQuery<out TResponse> : IRequest<TResponse>
{
}
