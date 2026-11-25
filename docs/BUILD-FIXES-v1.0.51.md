# iStCAD v1.0.52 — compile fixes

## ACadSharpCadFileService.cs

- Hatch boundary preview points are transformed with `toWorld`, the matrix parameter in the enclosing block traversal scope.
- `ProxyPreviewPath` is constructed with the case-correct named argument `Closed: true`, matching the record declaration.

These changes address compiler diagnostics CS0103 and CS1739.
