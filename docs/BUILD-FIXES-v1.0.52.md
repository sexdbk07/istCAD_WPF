# Build fixes – v1.0.52

## Fix

- Replaced the unsupported `Matrix4.ApplyTransform(XYZ)` call in Hatch INSERT preview generation with ACadSharp's supported `Matrix4 * XYZ` operator.
- Preserves the owning INSERT's accumulated `toWorld` matrix when converting each hatch preview point to world coordinates.

## Validation

- Source-level check confirms the unsupported call is removed from this code path.
- ACadSharp's vendored `Matrix4.Operators.cs` defines `operator *(Matrix4, XYZ)`.
- Full .NET build and AutoCAD/Windows runtime validation were not run in this environment.
