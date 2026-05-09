using Alcatraz.Domain.Abstractions;
using MediatR;

namespace Alcatraz.Application.Abstractions.Messaging;

public interface IQueryHandler<TQuery, TResponse> : IRequestHandler<TQuery, Result<TResponse>>
    where TQuery : IQuery<TResponse>
{
}