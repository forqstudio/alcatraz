using Alcatraz.Domain.Abstractions;
using MediatR;

namespace Alcatraz.Application.Abstractions.Messaging;

public interface IQuery<TResponse> : IRequest<Result<TResponse>>
{
}