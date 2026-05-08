FROM golang:1.25-alpine AS build
WORKDIR /src
COPY go.mod go.sum* ./
RUN go mod download || true
COPY . .
RUN CGO_ENABLED=0 GOOS=linux go build -trimpath -ldflags="-s -w" -o /out/alcatraz-routes ./cmd/alcatraz-routes

FROM alpine:3.20
RUN adduser -D -u 1000 routes
COPY --from=build /out/alcatraz-routes /usr/local/bin/alcatraz-routes
USER routes
ENTRYPOINT ["/usr/local/bin/alcatraz-routes"]
