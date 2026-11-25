using iStCAD_1.Core.Model;

namespace iStCAD_1.Application.Files;

public interface ICadFileService
{
    CadDrawing Open(string path, CancellationToken cancellationToken = default);
    void Save(CadDrawing drawing, string path);
}
