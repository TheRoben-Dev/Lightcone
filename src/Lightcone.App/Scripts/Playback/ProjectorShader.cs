namespace Lightcone.App.Playback;

/// <summary>
/// The projector intro's scene, in one fragment shader, kept deliberately minimal: a dark
/// auditorium seen from the side, and the light from the booth window. The lamp strikes with a
/// flicker, then a single soft cone of light spreads across the room through slow-moving haze.
/// Dust drifts through it and only shows where the light catches it, the film's frames make the
/// light inside the beam shimmer at 24 fps, and the whole picture has a fine grain and a faint gate
/// weave. No drawn machinery: the light is the subject. The timeline is driven from C#.
/// </summary>
public static class ProjectorShader
{
    public const string Code = """
        shader_type canvas_item;

        uniform vec2 size = vec2(1280.0, 800.0);
        uniform float t;          // seconds since the intro started
        uniform float lamp;       // 0..1 lamp brightness (the strike flicker is done on the CPU)
        uniform float spread;     // 0..1 how far the beam has opened up after the strike
        uniform vec2 drift = vec2(0.0);   // slow camera drift, in scene units
        uniform vec4 warm : source_color = vec4(1.0, 0.86, 0.66, 1.0);
        // The cinema screen the beam lands on. The camera move is a homography: h0..h2 map the
        // output back into the resting scene, g0..g2 map the scene into the screen's own 0..1
        // square (both computed on the CPU from the screen's corners).
        uniform vec3 h0 = vec3(1.0, 0.0, 0.0);
        uniform vec3 h1 = vec3(0.0, 1.0, 0.0);
        uniform vec3 h2 = vec3(0.0, 0.0, 1.0);
        uniform vec3 g0 = vec3(1.0, 0.0, 0.0);
        uniform vec3 g1 = vec3(0.0, 1.0, 0.0);
        uniform vec3 g2 = vec3(0.0, 0.0, 1.0);
        uniform float screen_x = 0.8;   // the screen's left edge in the resting scene
        uniform float push;       // 0..1 how far the camera has moved into the screen
        uniform float show;       // 0..1 the video replacing the blank light on the screen
        uniform float vid_aspect = 1.7778;
        uniform sampler2D video : filter_linear, hint_default_black;

        float hash(vec2 p) { return fract(sin(dot(p, vec2(127.1, 311.7))) * 43758.5453); }
        float noise(vec2 p) {
            vec2 i = floor(p); vec2 f = fract(p); f = f * f * (3.0 - 2.0 * f);
            return mix(mix(hash(i), hash(i + vec2(1.0, 0.0)), f.x), mix(hash(i + vec2(0.0, 1.0)), hash(i + vec2(1.0, 1.0)), f.x), f.y);
        }
        float fbm(vec2 p) {
            float v = 0.0; float a = 0.5;
            for (int i = 0; i < 5; i++) { v += a * noise(p); p = p * 2.03 + vec2(17.1, 9.2); a *= 0.5; }
            return v;
        }

        void fragment() {
            float aspect = size.x / size.y;
            // Scene space: y up, height 1, origin bottom left.
            vec3 o = vec3(UV, 1.0);
            vec3 hs = vec3(dot(h0, o), dot(h1, o), dot(h2, o));
            vec2 suv = hs.xy / hs.z;                    // where this pixel sits in the resting scene
            vec3 sv = vec3(suv, 1.0);
            vec3 gs = vec3(dot(g0, sv), dot(g1, sv), dot(g2, sv));
            vec2 loc = gs.xy / gs.z;                    // position on the screen, 0..1 inside it
            vec2 lpx = fwidth(loc) * 0.75;
            float inside = smoothstep(-lpx.x, lpx.x, loc.x) * smoothstep(-lpx.x, lpx.x, 1.0 - loc.x)
                         * smoothstep(-lpx.y, lpx.y, loc.y) * smoothstep(-lpx.y, lpx.y, 1.0 - loc.y);
            // The beam ends at the screen: nothing of it on or past the screen's plane.
            float past = smoothstep(screen_x - 0.035, screen_x + 0.002, suv.x);
            vec2 p = vec2(suv.x * aspect, 1.0 - suv.y) + drift;
            float frame = floor(t * 24.0);

            // The booth window, just off the top left; the beam heads right and slightly down.
            // A tiny per-frame weave, like film sitting in the gate.
            vec2 src = vec2(-0.06, 0.84) + (vec2(hash(vec2(frame, 1.0)), hash(vec2(frame, 2.0))) - 0.5) * 0.0012;
            vec2 dir = normalize(vec2(1.0, -0.2));
            vec2 v = p - src;
            float along = dot(v, dir);
            float across = dot(v, vec2(-dir.y, dir.x));
            float ang = atan(across, max(along, 1e-3));
            float half_w = mix(0.035, 0.15, spread);

            // Cone: soft edges, a brighter core, falling off with distance.
            float edge = 1.0 - smoothstep(half_w * 0.55, half_w, abs(ang));
            float core = 1.0 - smoothstep(0.0, half_w * 0.6, abs(ang));
            float cone = edge * (0.55 + 0.45 * core) * step(0.0, along);
            float fall = 1.0 / (1.0 + along * 0.9) + exp(-along * 2.5) * 0.8;

            // Haze: two layers of slow smoke, drifting apart. Density shapes the beam.
            vec2 hp = p * vec2(2.2, 3.2);
            float smoke = fbm(hp + vec2(t * 0.05, t * 0.018));
            smoke = mix(smoke, fbm(hp * 1.9 - vec2(t * 0.03, -t * 0.025) + 4.0), 0.45);
            float density = 0.18 + 1.5 * smoothstep(0.3, 0.8, smoke);

            // The picture inside the beam: fine streaks along the rays that change every film
            // frame, so the light shimmers like a projected film rather than a torch.
            float rays = noise(vec2(ang * 140.0, frame * 0.37)) * 0.6 + noise(vec2(ang * 48.0, frame * 0.21 + 3.0)) * 0.4;
            float picture = mix(0.82, 1.12, rays);
            float frame_flick = 0.97 + 0.03 * hash(vec2(frame, 7.0));

            float beam = cone * fall * density * picture * frame_flick * lamp * (1.0 - past);

            // Dust: three layers of motes drifting on slow currents; each shows only where the
            // light catches it, the nearer layers bigger and softer.
            float dust = 0.0;
            for (int l = 0; l < 3; l++) {
                float fl = float(l);
                float scale = 26.0 - fl * 7.0;
                vec2 q = p * scale + vec2(t * (0.25 + fl * 0.12), sin(t * 0.3 + fl) * 0.4 - t * (0.05 + fl * 0.03));
                vec2 cell = floor(q);
                vec2 f = fract(q);
                float h = hash(cell + fl * 13.0);
                if (h > 0.72) {
                    vec2 c = vec2(hash(cell + 1.7), hash(cell + 4.3)) * 0.7 + 0.15;
                    c += 0.12 * vec2(sin(t * (0.6 + h) + h * 30.0), cos(t * (0.5 + h) + h * 20.0));
                    float r = 0.035 + fl * 0.03;
                    float d = length(f - c);
                    float twinkle = 0.55 + 0.45 * sin(t * (2.0 + h * 3.0) + h * 40.0);
                    dust += (1.0 - smoothstep(r * 0.2, r, d)) * twinkle * (0.5 + fl * 0.35);
                }
            }
            dust *= cone * fall * lamp * 1.6 * (1.0 - past);

            // The window itself: a hot core with a soft bloom and a faint horizontal flare.
            vec2 w = p - src;
            float glow = exp(-length(w) * 14.0) * 2.6 + exp(-length(w) * 4.0) * 0.35;
            glow += exp(-abs(w.y) * 160.0) * exp(-abs(w.x) * 7.0) * 0.14;
            glow *= lamp;

            // Light scattered into the room: a faint warm lift around the beam.
            float scatter = exp(-abs(across) * 3.5) * step(-0.2, along) * 0.05 * lamp;

            vec3 tint = warm.rgb * vec3(1.05, 0.93, 0.78);
            vec3 col = vec3(0.012, 0.010, 0.012);
            col += tint * (beam * 1.05 + scatter);
            col += mix(tint, vec3(1.0), 0.4) * dust * 0.55;
            col += mix(tint, vec3(1.0), 0.6) * glow;

            // The screen: lit by the blank light of the beam while the film is threading, with a
            // soft spill onto the masking around it.
            vec2 lc = loc - 0.5;
            float out_d = max(max(abs(lc.x), abs(lc.y)) - 0.5, 0.0);
            col += tint * exp(-out_d * 18.0) * (1.0 - inside) * 0.05 * lamp * (1.0 - past * 0.5) * step(0.0, gs.z);
            // Brightest where the beam's core lands, falling off to the corners, with the film's
            // flicker and a little of the haze's texture in it.
            float hot = exp(-dot(lc * vec2(1.6, 2.2), lc * vec2(1.6, 2.2)) * 2.2);
            vec3 blank = mix(tint, vec3(1.0), 0.25) * lamp * frame_flick * (0.18 + 0.55 * hot) * (0.9 + 0.2 * smoke);
            col += blank * inside * (1.0 - show);

            // Filmic roll-off, vignette, grain (new every frame), and dither against banding.
            col = 1.0 - exp(-col * 1.35);

            // The film on the screen, fitted the way the player shows it (bars where it's needed),
            // so the end of the move is the player's own picture.
            float k = vid_aspect / aspect;
            vec2 vuv = k > 1.0 ? vec2(loc.x, (loc.y - 0.5) * k + 0.5) : vec2((loc.x - 0.5) / k + 0.5, loc.y);
            float in_pic = step(0.0, vuv.x) * step(vuv.x, 1.0) * step(0.0, vuv.y) * step(vuv.y, 1.0);
            vec3 film = texture(video, clamp(vuv, 0.0, 1.0)).rgb * in_pic;
            col = mix(col, film, inside * show);

            vec2 vc = UV - 0.5;
            col *= 1.0 - dot(vc, vc) * 0.9 * (1.0 - push);
            float grain = hash(floor(UV * size / 1.5) + frame * 1.37) - 0.5;
            col += grain * 0.035 * (1.0 - push);
            col += (hash(UV * size + 0.5) - 0.5) / 255.0 * (1.0 - push);
            COLOR = vec4(max(col, 0.0), 1.0);
        }
        """;
}
