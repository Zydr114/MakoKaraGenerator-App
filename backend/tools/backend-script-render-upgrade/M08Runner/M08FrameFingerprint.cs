using System.Text.Json;
using MakoKaraGenerator.Rendering;
using MakoKaraGenerator.Scripting;
using MakoKaraGenerator.Scripting.V2;

internal static class M08FrameFingerprint
{
    // A closed, explicit projection: serializing ParticleOrigin or ParticleSource as their base type loses fields.
    // Every output array retains production order; only the linked M07 text map projection sorts target maps.
    internal static byte[] Serialize(PreparedRenderFrame frame)
    {
        if (!frame.Particles.IsEmpty || frame.EmissionSchedule is not null)
            throw new InvalidOperationException("The finite M08 canonical format requires independent v2 particle output.");
        using MemoryStream stream = new();
        using (Utf8JsonWriter writer = new(stream))
        {
            writer.WriteStartObject(); writer.WriteString("schema", "mako-m08-frame-v1");
            writer.WritePropertyName("text");
            using (var text = JsonDocument.Parse(FrameFingerprint.Serialize(frame))) text.RootElement.WriteTo(writer);
            writer.WriteStartArray("owners");
            foreach (var owner in frame.OverlayOwners)
            {
                writer.WriteStartObject(); writer.WriteString("instance", owner.InstanceId.Value);
                writer.WriteString("artifact", owner.ArtifactHash); writer.WritePropertyName("package");
                if (owner.PackageIdentity is not { } package) writer.WriteNullValue();
                else
                {
                    writer.WriteStartObject(); writer.WriteString("id", package.PackageId);
                    writer.WriteString("revision", package.Revision); writer.WriteString("export", package.ExportId); writer.WriteEndObject();
                }
                writer.WriteEndObject();
            }
            writer.WriteEndArray(); writer.WriteStartArray("particleSnapshots");
            foreach (var snapshot in frame.V2ParticleSnapshots)
            {
                writer.WriteStartObject(); writer.WriteString("instance", snapshot.InstanceId.Value);
                writer.WriteStartArray("birthWindow"); writer.WriteNumberValue(snapshot.Births.Window.StartInclusiveUs);
                writer.WriteNumberValue(snapshot.Births.Window.EndInclusiveUs); writer.WriteEndArray();
                writer.WriteStartArray("births");
                foreach (var birth in snapshot.Births.Births) Birth(writer, birth);
                writer.WriteEndArray(); writer.WriteStartArray("alive");
                foreach (var view in snapshot.Alive) View(writer, view);
                writer.WriteEndArray(); writer.WriteStartArray("trails");
                foreach (var trail in snapshot.Trails)
                {
                    writer.WriteStartObject(); writer.WritePropertyName("id"); Identity(writer, trail.Id);
                    writer.WriteStartArray("samples");
                    foreach (var point in trail.Samples)
                    {
                        writer.WriteStartArray(); writer.WriteNumberValue(point.TimeUs);
                        writer.WriteNumberValue(point.Position.X); writer.WriteNumberValue(point.Position.Y); writer.WriteEndArray();
                    }
                    writer.WriteEndArray(); writer.WriteEndObject();
                }
                writer.WriteEndArray(); writer.WriteEndObject();
            }
            writer.WriteEndArray(); writer.WriteStartArray("flattenedAlive");
            foreach (var view in frame.V2Particles) View(writer, view);
            writer.WriteEndArray(); writer.WriteStartArray("overlayBatches");
            foreach (var batch in frame.OverlayBatches) Overlay(writer, batch);
            writer.WriteEndArray(); writer.WriteEndObject();
        }
        return stream.ToArray();
    }

    private static void Identity(Utf8JsonWriter writer, ParticleIdentity id)
    {
        writer.WriteStartObject(); writer.WriteString("instance", id.InstanceId.Value); writer.WriteString("emitter", id.EmitterId.Value);
        writer.WriteStartObject("origin");
        switch (id.Origin)
        {
            case ParticleOrigin.Continuous continuous:
                writer.WriteString("kind", "continuous"); writer.WritePropertyName("head"); Head(writer, continuous.Head);
                writer.WriteNumber("episodeStartUs", continuous.EpisodeStartUs);
                writer.WriteNumber("sequenceStartingAt1", continuous.SequenceStartingAt1); break;
            case ParticleOrigin.Burst burst:
                writer.WriteString("kind", "burst"); writer.WriteString("event", burst.EventId.Value);
                writer.WriteNumber("planOrdinal", burst.PlanOrdinal); writer.WriteNumber("particleOrdinal", burst.ParticleOrdinal);
                writer.WritePropertyName("anchor"); Source(writer, burst.Anchor); break;
            default: throw Unsupported(id.Origin);
        }
        writer.WriteEndObject(); writer.WriteEndObject();
    }

    private static void Head(Utf8JsonWriter writer, ParticleHead head)
    {
        writer.WriteStartObject();
        switch (head)
        {
            case ParticleHead.Normal normal:
                writer.WriteString("kind", "normal"); writer.WriteString("track", normal.Track.Value);
                writer.WriteString("binding", normal.Binding.Value); break;
            case ParticleHead.Bridge bridge:
                writer.WriteString("kind", "bridge"); writer.WritePropertyName("source"); Source(writer, bridge.Source); break;
            default: throw Unsupported(head);
        }
        writer.WriteEndObject();
    }

    private static void Source(Utf8JsonWriter writer, ParticleSource source)
    {
        writer.WriteStartObject(); writer.WriteString("binding", source.BindingId.Value);
        switch (source)
        {
            case ParticleSource.Cursor cursor:
                writer.WriteString("kind", "cursor"); writer.WriteString("track", cursor.Track.Value);
                writer.WritePropertyName("identity"); Cursor(writer, cursor.Identity); break;
            case ParticleSource.Bridge bridge:
                writer.WriteString("kind", "bridge"); writer.WriteString("track", bridge.Track.Value);
                writer.WriteString("transition", bridge.TransitionId.Value); writer.WritePropertyName("from"); Cursor(writer, bridge.From);
                writer.WritePropertyName("to"); Cursor(writer, bridge.To);
                writer.WriteStartArray("flightRange"); writer.WriteNumberValue(bridge.FlightRange.StartUs);
                writer.WriteNumberValue(bridge.FlightRange.EndUs); writer.WriteEndArray(); break;
            case ParticleSource.Token token:
                writer.WriteString("kind", "token"); writer.WriteString("token", token.Target.TokenId.Value); break;
            case ParticleSource.Line line:
                writer.WriteString("kind", "line"); writer.WriteString("line", line.Target.LineId.Value); break;
            case ParticleSource.TextMark mark:
                writer.WriteString("kind", "mark"); writer.WriteString("node", mark.Target.NodeId.Value);
                writer.WriteString("cue", mark.Target.CueId.Value); break;
            default: throw Unsupported(source);
        }
        writer.WriteEndObject();
    }

    private static void Cursor(Utf8JsonWriter writer, CursorIdentity identity)
    {
        writer.WriteStartObject(); writer.WriteString("branch", identity.BranchId.Value); writer.WriteString("line", identity.LineId.Value);
        writer.WriteString("binding", identity.BindingId.Value); writer.WriteString("token", identity.TokenId?.Value);
        writer.WriteString("occurrence", identity.OccurrenceId?.Value); writer.WriteString("progressSource", identity.ProgressSource.ToString());
        writer.WriteNumber("startUs", identity.TimeRange.StartUs); writer.WriteNumber("endUs", identity.TimeRange.EndUs); writer.WriteEndObject();
    }

    private static void Birth(Utf8JsonWriter writer, ParticleBirth birth)
    {
        writer.WriteStartObject(); writer.WritePropertyName("id"); Identity(writer, birth.Id);
        writer.WritePropertyName("source"); Source(writer, birth.Source); writer.WriteString("event", birth.EventId?.Value);
        writer.WriteNumber("birthUs", birth.BirthUs); writer.WriteNumber("deathUs", birth.DeathUs);
        Point(writer, "birthPosition", birth.BirthPosition); Vector(writer, "initialVelocity", birth.InitialVelocity);
        writer.WritePropertyName("spec"); Spec(writer, birth.Spec); writer.WriteEndObject();
    }

    private static void View(Utf8JsonWriter writer, ParticleView view)
    {
        writer.WriteStartObject(); writer.WritePropertyName("birth"); Birth(writer, view.Birth);
        Point(writer, "position", view.Position); writer.WriteNumber("sizePx", view.SizePx); Color(writer, "color", view.Color);
        writer.WriteString("rendering", view.Rendering.ToString()); writer.WriteString("layer", view.Layer.ToString()); writer.WriteEndObject();
    }

    private static void Spec(Utf8JsonWriter writer, ParticleSpec spec)
    {
        writer.WriteStartObject(); writer.WriteNumber("lifetimeUs", spec.LifetimeUs); writer.WriteNumber("sizePx", spec.SizePx);
        Color(writer, "color", spec.Color); Vector(writer, "velocity", spec.Velocity); writer.WriteNumber("spreadPx", spec.SpreadPx);
        writer.WriteNumber("trailDurationUs", spec.TrailDurationUs); writer.WriteString("rendering", spec.Rendering.ToString());
        writer.WriteString("layer", spec.Layer.ToString()); writer.WriteString("seedStreamKey", spec.SeedStreamKey);
        writer.WriteStartObject("motion"); Vector(writer, "acceleration", spec.Motion.Acceleration);
        writer.WritePropertyName("opacity"); Age(writer, spec.Motion.Opacity); writer.WritePropertyName("size"); Age(writer, spec.Motion.Size);
        writer.WriteEndObject(); writer.WriteEndObject();
    }

    private static void Age(Utf8JsonWriter writer, ParticleAgeCurve age)
    {
        writer.WriteStartObject(); writer.WriteNumber("start", age.Start); writer.WriteNumber("end", age.End);
        writer.WritePropertyName("curve"); Curve(writer, age.Curve); writer.WriteEndObject();
    }

    private static void Curve(Utf8JsonWriter writer, CurveValue curve)
    {
        writer.WriteStartObject();
        switch (curve)
        {
            case CurveValue.Easing easing:
                writer.WriteString("kind", "easing"); writer.WriteString("easing", easing.Kind.ToString()); break;
            case CurveValue.CubicBezier bezier:
                writer.WriteString("kind", "cubicBezier"); Point(writer, "first", bezier.First); Point(writer, "second", bezier.Second); break;
            case CurveValue.ControlPoints controls:
                writer.WriteString("kind", "controlPoints"); writer.WriteStartArray("points");
                foreach (var point in controls.Points)
                {
                    writer.WriteStartArray(); writer.WriteNumberValue(point.X); writer.WriteNumberValue(point.Y); writer.WriteEndArray();
                }
                writer.WriteEndArray(); break;
            default: throw Unsupported(curve);
        }
        writer.WriteEndObject();
    }

    private static void Overlay(Utf8JsonWriter writer, OverlayBatch batch)
    {
        writer.WriteStartObject(); writer.WriteString("instance", batch.InstanceId.Value); writer.WriteString("id", batch.Id.Value);
        writer.WriteString("layer", batch.Layer.ToString()); writer.WriteNumber("itemCount", batch.ItemCount);
        switch (batch)
        {
            case DotBatch dots:
                writer.WriteString("kind", "dots"); writer.WriteStartArray("dots");
                foreach (var dot in dots.Dots)
                {
                    writer.WriteStartObject(); Point(writer, "position", dot.Position); writer.WriteNumber("radius", dot.Radius);
                    Color(writer, "color", dot.Color); writer.WriteEndObject();
                }
                writer.WriteEndArray(); break;
            case SegmentBatch segments:
                writer.WriteString("kind", "segments"); writer.WriteStartArray("segments");
                foreach (var segment in segments.Segments)
                {
                    writer.WriteStartObject(); Point(writer, "start", segment.Start); Point(writer, "end", segment.End);
                    writer.WriteNumber("width", segment.Width); Color(writer, "color", segment.Color); writer.WriteEndObject();
                }
                writer.WriteEndArray(); break;
            case TrailBatch trails:
                writer.WriteString("kind", "trails"); writer.WriteStartArray("trails");
                foreach (var trail in trails.Trails)
                {
                    writer.WriteStartArray();
                    foreach (var point in trail.Points)
                    {
                        writer.WriteStartObject(); Point(writer, "position", point.Position); writer.WriteNumber("width", point.Width);
                        Color(writer, "color", point.Color); writer.WriteEndObject();
                    }
                    writer.WriteEndArray();
                }
                writer.WriteEndArray(); break;
            case SpriteBatch sprites:
                writer.WriteString("kind", "sprites"); writer.WriteStartArray("sprites");
                foreach (var sprite in sprites.Sprites)
                {
                    writer.WriteStartObject(); writer.WriteStartObject("image"); writer.WriteString("package", sprite.Image.Identity.PackageId);
                    writer.WriteString("revision", sprite.Image.Identity.Revision); writer.WriteString("resource", sprite.Image.Identity.ExportId);
                    writer.WriteEndObject(); writer.WriteStartArray("unitToWorld");
                    writer.WriteNumberValue(sprite.UnitToWorld.A); writer.WriteNumberValue(sprite.UnitToWorld.B);
                    writer.WriteNumberValue(sprite.UnitToWorld.C); writer.WriteNumberValue(sprite.UnitToWorld.D);
                    writer.WriteNumberValue(sprite.UnitToWorld.Tx); writer.WriteNumberValue(sprite.UnitToWorld.Ty); writer.WriteEndArray();
                    writer.WriteStartArray("source"); writer.WriteNumberValue(sprite.Source.Left); writer.WriteNumberValue(sprite.Source.Top);
                    writer.WriteNumberValue(sprite.Source.Right); writer.WriteNumberValue(sprite.Source.Bottom); writer.WriteEndArray();
                    Color(writer, "tint", sprite.Tint); writer.WriteEndObject();
                }
                writer.WriteEndArray(); break;
            default: throw Unsupported(batch);
        }
        writer.WriteEndObject();
    }

    private static void Point(Utf8JsonWriter writer, string name, Point2 point)
    { writer.WriteStartArray(name); writer.WriteNumberValue(point.X); writer.WriteNumberValue(point.Y); writer.WriteEndArray(); }
    private static void Vector(Utf8JsonWriter writer, string name, Vector2 vector)
    { writer.WriteStartArray(name); writer.WriteNumberValue(vector.X); writer.WriteNumberValue(vector.Y); writer.WriteEndArray(); }
    private static void Color(Utf8JsonWriter writer, string name, ColorRgba color)
    {
        writer.WriteStartArray(name); writer.WriteNumberValue(color.Red); writer.WriteNumberValue(color.Green);
        writer.WriteNumberValue(color.Blue); writer.WriteNumberValue(color.Alpha); writer.WriteEndArray();
    }
    private static InvalidOperationException Unsupported(object value)
        => new("Expand the finite M08 canonical format before sampling " + value.GetType().FullName + ".");
}
