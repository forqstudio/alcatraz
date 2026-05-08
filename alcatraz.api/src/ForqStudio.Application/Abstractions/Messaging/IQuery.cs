using ForqStudio.Domain.Abstractions;
using MediatR;

namespace ForqStudio.Application.Abstractions.Messaging;

public interface IQuery<TResponse> : IRequest<Result<TResponse>>
{
}