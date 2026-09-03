using System.Globalization;
using System.Text;

namespace DocManager.Photometry;

public sealed class LdtConverter
{
    private const double AngleTolerance = 0.000001;
    private const double CandelaClosureTolerance = 0.0001;
    private readonly IesParser _parser;
    private readonly PhotometryCatalogue _catalogue;

    public LdtConverter(IesParser? parser = null, PhotometryCatalogue? catalogue = null)
    {
        _parser = parser ?? new IesParser();
        _catalogue = catalogue ?? new PhotometryCatalogue();
    }

    public LdtConversionResult ConvertFile(string iesPath, IEnumerable<string>? cataloguePdfs, string outputDirectory, bool overwrite = false, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(iesPath);
        cancellationToken.ThrowIfCancellationRequested();
        var source = new IesSourceReader(_parser).ReadFile(iesPath);
        var canonicalName = DocManager.Core.ProductAssetName.CanonicalProductName(
            DocManager.Core.ProductAssetName.StripLegacySkuPrefix(LdtIdentity.SourceStem(source.SourcePath)));
        var pdfs = cataloguePdfs?.Where(File.Exists).OrderBy(path => path, StringComparer.OrdinalIgnoreCase).ToArray() ?? [];
        var selectedPdf = _catalogue.FindBestPdf(source.Document, pdfs, canonicalName);
        if (selectedPdf is null) throw new InvalidDataException($"Không tìm thấy PDF chính xác cho '{canonicalName}'.");
        return ConvertFileCore(source, selectedPdf, outputDirectory, canonicalName, overwrite, cancellationToken);
    }

    public LdtConversionResult ConvertFile(string iesPath, string cataloguePdfPath, string outputDirectory, string canonicalProductName, bool overwrite = false, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(iesPath);
        ArgumentException.ThrowIfNullOrWhiteSpace(cataloguePdfPath);
        if (!File.Exists(cataloguePdfPath)) throw new FileNotFoundException("Catalogue PDF không tồn tại.", cataloguePdfPath);
        return ConvertFileCore(new IesSourceReader(_parser).ReadFile(iesPath), cataloguePdfPath, outputDirectory, canonicalProductName, overwrite, cancellationToken);
    }

    public LdtConversionResult ConvertFileWithoutCatalogue(string iesPath, string outputDirectory, string canonicalProductName, bool overwrite = false, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(iesPath);
        return ConvertFileCore(new IesSourceReader(_parser).ReadFile(iesPath), null, outputDirectory, canonicalProductName, overwrite, cancellationToken);
    }

    private LdtConversionResult ConvertFileCore(IesSourceSnapshot source, string? cataloguePdfPath, string outputDirectory, string canonicalProductName, bool overwrite, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(source);
        ArgumentException.ThrowIfNullOrWhiteSpace(outputDirectory);
        cancellationToken.ThrowIfCancellationRequested();
        var canonical = DocManager.Core.ProductAssetName.CanonicalProductName(canonicalProductName);
        var ies = source.Document;
        var withoutCatalogue = string.IsNullOrWhiteSpace(cataloguePdfPath);
        var metadata = withoutCatalogue
            ? IesMetadataFallback.Create(ies)
            : _catalogue.ExtractPdf(cataloguePdfPath!, ies, cancellationToken);
        var warnings = IesMetadataFallback.Warnings(metadata, ies, withoutCatalogue)
            .Select(warning => warning.Message)
            .Concat(metadata.GeometryWarnings)
            .ToArray();
        cancellationToken.ThrowIfCancellationRequested();
        var output = Path.Combine(Path.GetFullPath(outputDirectory), $"{DocManager.Core.ProductAssetName.CanonicalBaseName(canonical)}.ldt");
        if (File.Exists(output) && !overwrite)
            throw new IOException($"LDT canonical đã tồn tại: {output}");
        var content = Convert(ies, metadata, canonical, out var symmetry, out var flux, out var power);
        cancellationToken.ThrowIfCancellationRequested();
        Directory.CreateDirectory(Path.GetDirectoryName(output)!);
        var temporary = $"{output}.{Guid.NewGuid():N}.part";
        try
        {
            File.WriteAllText(temporary, content, new UTF8Encoding(false));
            cancellationToken.ThrowIfCancellationRequested();
            if (overwrite)
            {
                DocManager.Core.AtomicFile.Replace(temporary, output);
            }
            else
            {
                File.Move(temporary, output, false);
            }
        }
        finally
        {
            if (File.Exists(temporary)) File.Delete(temporary);
        }

        var provenance = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            ["flux"] = metadata.LuminousFluxProvenance,
            ["power"] = metadata.InputWattsProvenance,
            ["cri"] = metadata.CriProvenance,
            ["cct"] = metadata.CctProvenance,
            ["dimensions"] = metadata.OverallGeometry?.Provenance ?? "ies:physical-dimensions"
        };
        return new LdtConversionResult(output, symmetry, flux, power, warnings, source.SourcePath, withoutCatalogue ? string.Empty : Path.GetFullPath(cataloguePdfPath!), canonical, provenance, source.Fingerprint);
    }

    public string Convert(IesDocument ies, PhotometryMetadata? metadata, out int symmetryIndicator, out double fluxLumens, out double powerWatts) =>
        Convert(ies, metadata, DocManager.Core.ProductAssetName.CanonicalProductName(ies.LuminaireName), out symmetryIndicator, out fluxLumens, out powerWatts);

    public string Convert(IesDocument ies, PhotometryMetadata? metadata, string canonicalProductName, out int symmetryIndicator, out double fluxLumens, out double powerWatts)
    {
        ArgumentNullException.ThrowIfNull(ies);
        Validate(ies);
        metadata ??= new PhotometryMetadata();
        var canonicalName = DocManager.Core.ProductAssetName.CanonicalProductName(canonicalProductName);

        var catalogueFlux = PositiveFinite(metadata.LuminousFluxLumens);
        var iesFlux = ies.TotalLumens > 0 && double.IsFinite(ies.TotalLumens) ? ies.TotalLumens : (double?)null;
        var isAbsolute = ies.LumensPerLamp == -1;
        if (isAbsolute && catalogueFlux is null)
        {
            throw new InvalidDataException("Absolute IES photometry (lumens per lamp = -1) requires a positive catalogue technical luminaire flux for Eulumdat normalization.");
        }

        fluxLumens = catalogueFlux ?? iesFlux
            ?? throw new InvalidDataException("IES conversion requires a positive IES or catalogue technical luminous flux for Eulumdat normalization.");
        powerWatts = NonNegativeFinite(metadata.InputWatts) ?? (ies.InputWatts >= 0 && double.IsFinite(ies.InputWatts) ? ies.InputWatts : 0);

        var layout = BuildPlaneLayout(ies);
        symmetryIndicator = layout.Symmetry;
        var gammaAngles = BuildGammaAngles(ies.VerticalAngles);
        var (downwardFlux, integratedFlux) = IntegrateFlux(ies, layout);
        var dffPercent = 100 * downwardFlux / integratedFlux;
        var lorlPercent = 100 * integratedFlux / fluxLumens;

        // Eulumdat distribution values remain cd/klm for both relative and
        // absolute photometry. Absolute data is identified by a negative 26a.
        var scale = 1000d / fluxLumens;
        var unitToMillimetres = ies.UnitType == 1 ? 304.8 : 1000d;
        var iesLength = Math.Abs(ies.Length) * unitToMillimetres;
        var iesWidth = Math.Abs(ies.Width) * unitToMillimetres;
        var iesHeight = Math.Abs(ies.Height) * unitToMillimetres;
        var overall = IsValidOverallGeometry(metadata.OverallGeometry) ? metadata.OverallGeometry : null;
        var length = overall?.LengthOrDiameterMm ?? iesLength;
        var width = overall?.WidthMm ?? iesWidth;
        var height = overall?.HeightMm ?? iesHeight;
        var luminous = metadata.LuminousArea;
        var luminousLength = PositiveFinite(luminous?.LengthOrDiameterMm) ?? length;
        var luminousWidth = PositiveFinite(luminous?.WidthMm) ?? width;
        var luminousHeightC0 = PositiveFinite(luminous?.HeightC0Mm) ?? height;
        var typeIndicator = DetermineTypeIndicator(overall, length, width, symmetryIndicator);
        var cStep = UniformStep(layout.FullPlanes, true);
        var gammaStep = UniformStep(gammaAngles, false);
        var lampCount = Math.Max(1, ies.LampCount) * (isAbsolute ? -1 : 1);
        var lines = new List<string>
        {
            Safe(ies.Manufacturer, "Unknown"),                         // 1: manufacturer is IES-only
            typeIndicator.ToString(CultureInfo.InvariantCulture),      // 2 Ityp
            symmetryIndicator.ToString(CultureInfo.InvariantCulture),  // 3 Isym
            layout.FullPlanes.Count.ToString(CultureInfo.InvariantCulture), // 4 Mc: full 0..<360 grid
            Format(cStep, "0.######"),                                 // 5 Dc; 0 means irregular
            gammaAngles.Count.ToString(CultureInfo.InvariantCulture),  // 6 Ng
            Format(gammaStep, "0.######"),                             // 7 Dg; 0 means irregular
            ies.GetHeader("TEST") ?? string.Empty,                     // 8
            Safe(canonicalName),                                       // 9 canonical product name
            string.Empty,                                              // 10 intentionally blank
            Safe(canonicalName),                                       // 11 canonical product name / filename identity
            ProvenanceLine(metadata),                                  // 12
            Format(length, "0.##"),                                    // 13 length
            Format(width, "0.##"),                                     // 14 width
            Format(height, "0.##"),                                    // 15 height
            Format(luminousLength, "0.##"),                            // 16 luminous length
            Format(luminousWidth, "0.##"),                             // 17 luminous width
            Format(luminousHeightC0, "0.##"),                         // 18 luminous height C0
            Format(NonNegativeFinite(luminous?.HeightC90Mm) ?? 0, "0.##"),  // 19 luminous height C90
            Format(NonNegativeFinite(luminous?.HeightC180Mm) ?? 0, "0.##"), // 20 luminous height C180
            Format(NonNegativeFinite(luminous?.HeightC270Mm) ?? 0, "0.##"), // 21 luminous height C270
            Format(dffPercent, "0.0"),                                 // 22 downward flux ratio, percent
            Format(lorlPercent, "0.0"),                                // 23 luminaire light-output ratio, percent
            "1.0",                                                     // 24 conversion factor
            "0.0",                                                     // 25 tilt
            "1",                                                       // 26 lamp sets
            lampCount.ToString(CultureInfo.InvariantCulture),          // 27 (26a); negative means absolute
            Safe(ies.Lamp, "LED"),                                     // 28 (26b)
            Format(fluxLumens, "0.###"),                                // 29 (26c), positive technical flux
            (metadata.CctKelvin ?? 0).ToString(CultureInfo.InvariantCulture), // 30
            (metadata.Cri ?? 0).ToString(CultureInfo.InvariantCulture),       // 31
            Format(powerWatts, "0.#")                                  // 32
        };
        // Direct-ratio fields are retained from the canonical converter. They
        // are not used to state DFF/LORL, which are derived above.
        lines.AddRange(Enumerable.Repeat("0", 10));                    // 33-42
        lines.AddRange(layout.FullPlanes.Select(value => Format(value, "0.####")));
        lines.AddRange(gammaAngles.Select(value => Format(value, "0.####")));
        foreach (var cPlane in layout.PayloadPlanes)
        {
            foreach (var gamma in gammaAngles)
            {
                var candela = gamma > ies.VerticalAngles[^1] + AngleTolerance
                    ? 0
                    : InterpolateCandela(ies, cPlane, gamma, layout.Symmetry);
                lines.Add(Format(candela * scale, "0.####"));
            }
        }

        return string.Join("\r\n", lines) + "\r\n";
    }

    private static PlaneLayout BuildPlaneLayout(IesDocument ies)
    {
        var angles = ies.HorizontalAngles;
        var min = angles[0];
        var max = angles[^1];
        if (angles.Count == 1)
        {
            if (Math.Abs(min) > AngleTolerance)
            {
                throw new InvalidDataException("A rotationally symmetric Type C IES must contain its single horizontal plane at C0.");
            }
            return new PlaneLayout(1, [0], [0]);
        }

        if (Math.Abs(min) <= AngleTolerance && Math.Abs(max - 90) <= AngleTolerance)
        {
            if (UniformStep(angles, false) <= 0)
            {
                throw new InvalidDataException("Quarter-symmetric IES grid cannot form the official full C planes because its source spacing is irregular.");
            }
            var full = ExpandAngles(angles, angle => 180 - angle, angle => 180 + angle, angle => 360 - angle);
            var layout = new PlaneLayout(4, full, angles.ToArray());
            if (!IsOfficialLayout(layout))
            {
                throw new InvalidDataException("Quarter-symmetric IES grid cannot form the official full C-plane count required by Eulumdat.");
            }
            return layout;
        }

        if (Math.Abs(min) <= AngleTolerance && Math.Abs(max - 180) <= AngleTolerance)
        {
            if (UniformStep(angles, false) <= 0)
            {
                throw new InvalidDataException("Half-symmetric IES grid cannot form the official full C planes because its source spacing is irregular.");
            }
            var full = ExpandAngles(angles, angle => 360 - angle);
            var layout = new PlaneLayout(2, full, angles.ToArray());
            if (!IsOfficialLayout(layout))
            {
                throw new InvalidDataException("Half-symmetric IES grid cannot form the official full C-plane count required by Eulumdat.");
            }
            return layout;
        }

        // Isym=3 requires its measured source half to be C90..C270. The LDT
        // payload order is the official wrapped C270..C90 order.
        if (Math.Abs(min - 90) <= AngleTolerance && Math.Abs(max - 270) <= AngleTolerance && UniformStep(angles, false) > 0)
        {
            var full = ExpandAngles(angles, angle => NormalizeAngle(180 - angle));
            var payload = full.Where(angle => angle >= 270 - AngleTolerance)
                .Concat(full.Where(angle => angle <= 90 + AngleTolerance))
                .ToArray();
            var layout = new PlaneLayout(3, full, payload);
            return IsOfficialLayout(layout) ? layout : NonsymmetricLayout(ies);
        }

        return NonsymmetricLayout(ies);
    }

    private static bool IsOfficialLayout(PlaneLayout layout) => layout.Symmetry switch
    {
        2 or 3 => layout.FullPlanes.Count % 2 == 0 && layout.PayloadPlanes.Count == (layout.FullPlanes.Count / 2) + 1,
        4 => layout.FullPlanes.Count % 4 == 0 && layout.PayloadPlanes.Count == (layout.FullPlanes.Count / 4) + 1,
        _ => true
    };

    private static PlaneLayout NonsymmetricLayout(IesDocument ies)
    {
        var planes = ies.HorizontalAngles.Where(angle => angle < 360 - AngleTolerance).Distinct().OrderBy(angle => angle).ToArray();
        if (planes.Length < 2 || Math.Abs(planes[0]) > AngleTolerance)
        {
            throw new InvalidDataException("A non-symmetric Eulumdat grid must contain full C planes starting at C0.");
        }
        return new PlaneLayout(0, planes, planes);
    }

    private static IReadOnlyList<double> ExpandAngles(IReadOnlyList<double> source, params Func<double, double>[] mirrors) =>
        source.Concat(mirrors.SelectMany(mirror => source.Select(mirror)))
            .Select(NormalizeAngle)
            .DistinctBy(angle => Math.Round(angle, 6))
            .OrderBy(angle => angle)
            .ToArray();

    private static IReadOnlyList<double> BuildGammaAngles(IReadOnlyList<double> source)
    {
        if (Math.Abs(source[0]) > AngleTolerance)
        {
            throw new InvalidDataException("Eulumdat conversion requires Gamma angles to start at 0 degrees.");
        }
        if (Math.Abs(source[^1] - 180) <= AngleTolerance) return source.ToArray();
        if (Math.Abs(source[^1] - 90) > AngleTolerance)
        {
            throw new InvalidDataException("Eulumdat conversion requires Gamma coverage ending at exactly 90 or 180 degrees.");
        }

        var step = UniformStep(source, false);
        if (step <= 0 || Math.Abs((180 / step) - Math.Round(180 / step)) > AngleTolerance)
        {
            throw new InvalidDataException("A Gamma 0..90 source must have a uniform step that can be extended explicitly to 180 degrees.");
        }
        var count = checked((int)Math.Round(180 / step)) + 1;
        return Enumerable.Range(0, count).Select(index => Math.Round(index * step, 6)).ToArray();
    }

    private static double UniformStep(IReadOnlyList<double> values, bool requireCyclicClosure)
    {
        if (values.Count < 2) return 0;
        var step = values[1] - values[0];
        if (step <= AngleTolerance) return 0;
        for (var index = 2; index < values.Count; index++)
        {
            if (Math.Abs((values[index] - values[index - 1]) - step) > AngleTolerance) return 0;
        }
        if (requireCyclicClosure && Math.Abs((360 - values[^1] + values[0]) - step) > AngleTolerance) return 0;
        return step;
    }

    private static (double Downward, double Total) IntegrateFlux(IesDocument ies, PlaneLayout layout)
    {
        if (Math.Abs(ies.VerticalAngles[0]) > AngleTolerance ||
            (Math.Abs(ies.VerticalAngles[^1] - 90) > AngleTolerance && Math.Abs(ies.VerticalAngles[^1] - 180) > AngleTolerance))
        {
            throw new InvalidDataException("DFF and LORL require Gamma coverage from 0 to exactly 90 or 180 degrees.");
        }

        var downwardByPlane = layout.FullPlanes.Select(c => IntegrateGamma(ies, c, layout.Symmetry, 0, 90)).ToArray();
        // A source ending at Gamma 90 explicitly represents a downlight with zero
        // intensity in the omitted upper hemisphere; therefore total equals downward.
        var totalByPlane = Math.Abs(ies.VerticalAngles[^1] - 90) <= AngleTolerance
            ? downwardByPlane
            : layout.FullPlanes.Select(c => IntegrateGamma(ies, c, layout.Symmetry, 0, 180)).ToArray();
        var downward = IntegrateC(layout.FullPlanes, downwardByPlane);
        var total = IntegrateC(layout.FullPlanes, totalByPlane);
        if (!double.IsFinite(total) || total <= 0 || !double.IsFinite(downward) || downward < 0)
        {
            throw new InvalidDataException("Candela distribution does not yield a positive finite spherical luminous flux.");
        }
        return (downward, total);
    }

    private static double IntegrateGamma(IesDocument ies, double cPlane, int symmetry, double startDegrees, double endDegrees)
    {
        var angles = ies.VerticalAngles.Where(angle => angle > startDegrees + AngleTolerance && angle < endDegrees - AngleTolerance)
            .Prepend(startDegrees).Append(endDegrees).ToArray();
        var total = 0d;
        for (var index = 1; index < angles.Length; index++)
        {
            var a = DegreesToRadians(angles[index - 1]);
            var b = DegreesToRadians(angles[index]);
            var first = InterpolateCandela(ies, cPlane, angles[index - 1], symmetry);
            var second = InterpolateCandela(ies, cPlane, angles[index], symmetry);
            var slope = (second - first) / (b - a);
            total += first * (Math.Cos(a) - Math.Cos(b));
            total += slope * (-(b - a) * Math.Cos(b) + Math.Sin(b) - Math.Sin(a));
        }
        return total;
    }

    private static double IntegrateC(IReadOnlyList<double> planes, IReadOnlyList<double> values)
    {
        if (planes.Count == 1) return 2 * Math.PI * values[0];
        var total = 0d;
        for (var index = 0; index < planes.Count; index++)
        {
            var next = (index + 1) % planes.Count;
            var delta = next == 0 ? 360 - planes[index] + planes[0] : planes[next] - planes[index];
            total += 0.5 * (values[index] + values[next]) * DegreesToRadians(delta);
        }
        return total;
    }

    private static double InterpolateCandela(IesDocument ies, double horizontal, double vertical, int symmetry)
    {
        var sourceHorizontal = FoldHorizontal(horizontal, symmetry);
        var h = Bounds(ies.HorizontalAngles, sourceHorizontal);
        var v = Bounds(ies.VerticalAngles, vertical);
        var topLeft = ies.Candela[h.Lower][v.Lower];
        var topRight = ies.Candela[h.Lower][v.Upper];
        var bottomLeft = ies.Candela[h.Upper][v.Lower];
        var bottomRight = ies.Candela[h.Upper][v.Upper];
        var top = Lerp(topLeft, topRight, v.Fraction);
        var bottom = Lerp(bottomLeft, bottomRight, v.Fraction);
        return Lerp(top, bottom, h.Fraction);
    }

    private static double FoldHorizontal(double angle, int symmetry)
    {
        angle = NormalizeAngle(angle);
        return symmetry switch
        {
            1 => 0,
            2 => angle <= 180 ? angle : 360 - angle,
            3 when angle < 90 || angle > 270 => NormalizeAngle(180 - angle),
            4 when angle <= 90 => angle,
            4 when angle <= 180 => 180 - angle,
            4 when angle <= 270 => angle - 180,
            4 => 360 - angle,
            _ => angle
        };
    }

    private static (int Lower, int Upper, double Fraction) Bounds(IReadOnlyList<double> values, double value)
    {
        if (values.Count == 1 || value <= values[0]) return (0, 0, 0);
        if (value >= values[^1]) return (values.Count - 1, values.Count - 1, 0);
        for (var index = 1; index < values.Count; index++)
        {
            if (value <= values[index])
            {
                var range = values[index] - values[index - 1];
                return (index - 1, index, range <= 0 ? 0 : (value - values[index - 1]) / range);
            }
        }
        return (values.Count - 1, values.Count - 1, 0);
    }

    private static void Validate(IesDocument ies)
    {
        if (ies.PhotometricType != 1) throw new NotSupportedException("Only Type C photometry can be serialized without a coordinate transform.");
        if (ies.UnitType is < 1 or > 2) throw new InvalidDataException("IES unit type must be 1 or 2.");
        if (ies.HorizontalAngles.Count == 0 || ies.VerticalAngles.Count == 0) throw new InvalidDataException("IES angle grids cannot be empty.");
        ValidateStrictlyIncreasingAngles(ies.VerticalAngles, 0, 180, "Gamma");
        ValidateStrictlyIncreasingAngles(ies.HorizontalAngles, 0, 360, "C");
        if (ies.Candela.Count != ies.HorizontalAngles.Count || ies.Candela.Any(row => row.Count != ies.VerticalAngles.Count))
        {
            throw new InvalidDataException("IES candela matrix dimensions do not match the angle grids.");
        }
        if (ies.Candela.SelectMany(row => row).Any(value => !double.IsFinite(value) || value < 0))
        {
            throw new InvalidDataException("IES candela values must be finite and non-negative.");
        }
        if (ies.HorizontalAngles.Count > 1 &&
            Math.Abs(ies.HorizontalAngles[0]) <= AngleTolerance &&
            Math.Abs(ies.HorizontalAngles[^1] - 360) <= AngleTolerance &&
            !CandelaRowsAgree(ies.Candela[0], ies.Candela[^1]))
        {
            throw new InvalidDataException("The C360 candela plane must match C0 when both planes are present.");
        }
    }

    private static void ValidateStrictlyIncreasingAngles(
        IReadOnlyList<double> values,
        double minimum,
        double maximum,
        string field)
    {
        for (var index = 0; index < values.Count; index++)
        {
            var value = values[index];
            if (!double.IsFinite(value) || value < minimum - AngleTolerance || value > maximum + AngleTolerance)
                throw new InvalidDataException($"IES {field} angles must be finite and within {minimum}..{maximum} degrees.");
            if (index > 0 && value - values[index - 1] <= AngleTolerance)
                throw new InvalidDataException($"IES {field} angles must be strictly increasing.");
        }
    }

    private static bool CandelaRowsAgree(IReadOnlyList<double> first, IReadOnlyList<double> second)
    {
        for (var index = 0; index < first.Count; index++)
        {
            if (Math.Abs(first[index] - second[index]) > CandelaClosureTolerance * Math.Max(1, Math.Abs(first[index])))
                return false;
        }
        return true;
    }

    private static bool IsValidOverallGeometry(PhotometryGeometry? geometry) =>
        geometry is not null &&
        double.IsFinite(geometry.LengthOrDiameterMm) && geometry.LengthOrDiameterMm >= 0 &&
        double.IsFinite(geometry.WidthMm) && geometry.WidthMm >= 0 &&
        double.IsFinite(geometry.HeightMm) && geometry.HeightMm >= 0;

    private static int DetermineTypeIndicator(PhotometryGeometry? overallGeometry, double length, double width, int symmetry)
    {
        if (overallGeometry?.Shape == PhotometryGeometryShape.Circular)
            return symmetry == 1 ? 1 : 3;

        var shortest = Math.Min(length > 0 ? length : 1, width > 0 ? width : 1);
        var isLinear = Math.Max(length, width) > 4 * Math.Max(1, shortest);
        return isLinear ? 2 : symmetry == 1 ? 1 : 3;
    }

    private static string ProvenanceLine(PhotometryMetadata metadata)
    {
        var catalogue = new[]
        {
            metadata.LuminousFluxProvenance,
            metadata.InputWattsProvenance,
            metadata.CriProvenance,
            metadata.CctProvenance
        }.Where(value => value.StartsWith("catalogue:", StringComparison.OrdinalIgnoreCase))
            .Select(value => value["catalogue:".Length..])
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray();
        var overrides = catalogue.Length == 0 ? "none" : string.Join(',', catalogue);
        var dimensions = metadata.OverallGeometry is null ? "IES fallback" : "catalogue overall";
        return $"Converted by DocManager; technical provenance: catalogue={overrides}; manufacturer/lamp=IES; dimensions={dimensions}; luminous-area={(metadata.LuminousArea is null ? "none" : "catalogue")}; DFF/LORL=Type C candela";
    }

    private static double NormalizeAngle(double angle)
    {
        var normalized = ((angle % 360) + 360) % 360;
        return Math.Abs(normalized - 360) <= AngleTolerance || Math.Abs(normalized) <= AngleTolerance ? 0 : Math.Round(normalized, 6);
    }

    private static double DegreesToRadians(double degrees) => degrees * Math.PI / 180;
    private static double? PositiveFinite(double? value) => value is > 0 && double.IsFinite(value.Value) ? value : null;
    private static double? NonNegativeFinite(double? value) => value is >= 0 && double.IsFinite(value.Value) ? value : null;
    private static double Lerp(double first, double second, double fraction) => first + ((second - first) * fraction);
    private static string Format(double value, string format) => value.ToString(format, CultureInfo.InvariantCulture);
    private static string Safe(params string?[] candidates) => candidates.FirstOrDefault(value => !string.IsNullOrWhiteSpace(value))?.Replace("\r", " ").Replace("\n", " ").Trim() ?? string.Empty;

    private sealed record PlaneLayout(int Symmetry, IReadOnlyList<double> FullPlanes, IReadOnlyList<double> PayloadPlanes);
}
