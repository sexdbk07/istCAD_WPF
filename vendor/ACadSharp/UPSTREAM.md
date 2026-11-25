# Vendored CAD source

- ACadSharp upstream: https://github.com/DomCR/ACadSharp
- ACadSharp source version: 3.8.36
- ACadSharp source commit: `25b534813c1e5beb19ce78dd8f671781fd4d052f`
- ACadSharp license: MIT; see `LICENSE`.
- Shared CSUtilities upstream: https://github.com/DomCR/CSUtilities
- Shared CSUtilities commit: `a6505ab24bb45eac91e912179b2049136b7e88af`
- Shared source license: MIT; see `src/CSUtilities/LICENSE`.

ACadSharp, CSMath and CSUtilities are compiled from the checked-in source directly into the `iStCAD_1.Infras` project. No separate ACadSharp project reference, NuGet package or hosted API is used. Upstream tests, documentation assets and sample drawings are not included in this app distribution.

## Local source delta

- Added non-materializing `CadObject.HasExtendedData` plus `ExtendedDataDictionary.Count` and `RecordCount` so iSt-CAD can inventory XData without allocating empty dictionaries on every database object. The original XData collections and writer behavior are unchanged.
