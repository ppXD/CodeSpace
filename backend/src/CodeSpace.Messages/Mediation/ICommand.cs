using MediatR;

namespace CodeSpace.Messages.Mediation;

public interface ICommand<out TResponse> : IRequest<TResponse>
{
}
