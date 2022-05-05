# Version history / migration notes

## 3.0.0 (in progress)

Supports building user contexts after authentication has occurred for WebSocket
connections; supports and returns media type of `application/graphql+json` by default.

Support for ASP.NET Core 2.1 added, tested with .NET Core 2.1 and .NET Framework 4.8.

## 2.1.0

Authentication validation rule and support

New features:

- Added codes to middleware errors
- Added validation rule for schema authorization; supports authorization rules set on
  the schema, output graphs, fields of output graphs, and query arguments.  Skips fields
  appropriately if marked with `@skip` or `@include`.  Does not check authorization rules
  set on input graphs or input fields.
- Added authorization options to middleware to authenticate connection prior to execution
  of request.

## 1.0.0

Initial release

Added features over GraphQL.Server:

- Single `UseGraphQL` call to enable middleware including WebSocket support
- Options to enable/disable GET, POST and/or WebSocket requests
- Options to enable/disable batched requests and/or executed batched requests in parallel
- Options to enable/disable reading variables and extensions from the query string
- Option to enable/disable reading query string for POST requests
- Option to return OK or BadRequest when a validation error occurs
- More virtual methods allowing to override specific behavior
- WebSocket support of new graphql-ws protocol (see https://github.com/enisdenjo/graphql-ws)
- Compatible with GraphQL v5
