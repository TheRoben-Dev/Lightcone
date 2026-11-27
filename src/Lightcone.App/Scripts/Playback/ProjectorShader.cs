namespace Lightcone.App.Playback;

/// <summary>
/// The projector intro's scene, drawn in one fragment shader: a shaded side view of a 35 mm
/// projector (lamp house with glowing vents, brushed body, sprocket wheels, ringed lens), two reels
/// with cut-out windows that blur as they spin, film threading through with moving sprocket holes,
/// and a volumetric beam with smoke, dust, bloom and a lamp that flickers as it warms up. The
/// timeline (drop, spin, lamp, camera) is driven from C# through the uniforms.
/// </summary>
public static class ProjectorShader
{
    public const string Code = """
        shader_type canvas_item;

        uniform vec2 size = vec2(1280.0, 800.0);
        uniform float t;          // seconds since the intro started
        uniform float ang;        // front reel angle (rad)
        uniform float speed;      // 0..1 motor speed (motion blur, film travel)
        uniform float film_pos;   // film travel, in scene units
        uniform float drop;       // 0 = reel above, 1 = seated (eased with overshoot)
        uniform float lamp;       // 0..1 lamp brightness (warm-up flicker done on the CPU)
        uniform float zoom = 1.0;
        uniform vec2 cam = vec2(0.0);
        uniform float alpha = 1.0;
        uniform vec4 warm : source_color = vec4(1.0, 0.85, 0.62, 1.0);
        uniform vec4 accent : source_color = vec4(0.88, 0.19, 0.25, 1.0);

        const vec2 L = vec2(-0.55, 0.83);     // key light, top left
        const vec3 METAL = vec3(0.13, 0.135, 0.15);
        const vec3 METAL_HI = vec3(0.62, 0.64, 0.68);

        float hash(vec2 p) { return fract(sin(dot(p, vec2(127.1, 311.7))) * 43758.5453); }
        float noise(vec2 p) {
            vec2 i = floor(p); vec2 f = fract(p); f = f * f * (3.0 - 2.0 * f);
            return mix(mix(hash(i), hash(i + vec2(1.0, 0.0)), f.x), mix(hash(i + vec2(0.0, 1.0)), hash(i + vec2(1.0, 1.0)), f.x), f.y);
        }
        float sd_box(vec2 p, vec2 b, float r) { vec2 q = abs(p) - b + r; return length(max(q, 0.0)) + min(max(q.x, q.y), 0.0) - r; }
        float sd_seg(vec2 p, vec2 a, vec2 b, out float h) {
            vec2 pa = p - a; vec2 ba = b - a; h = clamp(dot(pa, ba) / dot(ba, ba), 0.0, 1.0); return length(pa - ba * h);
        }
        mat2 rot(float a) { float c = cos(a); float s = sin(a); return mat2(vec2(c, s), vec2(-s, c)); }

        // Coverage of a shape from its distance, anti-aliased over one pixel.
        float fill(float d, float px) { return 1.0 - smoothstep(-px, px, d); }

        // Bevel lighting for a shape: a lit rim towards the light, a dark rim away from it.
        vec3 bevel(vec3 base, float d, float w) {
            vec2 g = vec2(dFdx(d), -dFdy(d));
            g = g / max(length(g), 1e-5);
            float band = smoothstep(-w, 0.0, d);
            float lit = dot(g, L);
            return base + METAL_HI * max(lit, 0.0) * band * 0.55 - base * max(-lit, 0.0) * band * 0.6;
        }

        // Brushed metal: a vertical light falloff plus fine horizontal streaks.
        vec3 brushed(vec2 p, vec3 base, float top) {
            float streak = noise(vec2(p.x * 3.0, p.y * 420.0)) * 0.05 + noise(vec2(p.x * 12.0, p.y * 900.0)) * 0.03;
            return base * (0.75 + 0.5 * smoothstep(top - 0.4, top, p.y)) + streak;
        }

        // Reel window mask (three kidney cut-outs) at angle a, radius share rr.
        float windows(float a, float rr) {
            float seg = 6.2831853 / 3.0;
            float k = mod(a, seg) / seg;                         // 0..1 inside one third
            float ang_in = smoothstep(0.08, 0.14, k) * (1.0 - smoothstep(0.62, 0.68, k));
            float rad_in = smoothstep(0.33, 0.37, rr) * (1.0 - smoothstep(0.80, 0.84, rr));
            return ang_in * rad_in;
        }

        // Draws one reel over col. Returns the updated colour.
        vec3 reel(vec2 p, vec2 c, float r, float a, float blur, float wound, vec3 col, float px) {
            vec2 q = p - c;
            float rad = length(q);
            float rr = rad / r;
            if (rr > 1.08) { return col; }
            float th = atan(q.y, q.x);
            // Motion blur: average the window mask over the angle swept during a frame.
            float m = 0.0;
            for (int i = 0; i < 7; i++) { m += windows(th - a - blur * float(i) / 6.0, rr); }
            m /= 7.0;
            // Flange: satin metal with an anisotropic streak that stays put while the reel turns.
            vec2 n = q / max(rad, 1e-4);
            float aniso = exp(-pow(dot(n, vec2(L.y, -L.x)), 2.0) * 18.0);
            vec3 flange = METAL * 1.25 + METAL_HI * aniso * 0.22 * smoothstep(0.2, 0.9, rr);
            flange += (noise(vec2(th * 40.0, rr * 3.0)) - 0.5) * 0.02;
            // Wound film behind the windows: dark amber with fine rings.
            float filmed = 1.0 - smoothstep(wound - 0.01, wound + 0.01, rr);
            vec3 film = vec3(0.16, 0.085, 0.05) * (0.8 + 0.25 * noise(vec2(rr * 260.0, 0.0))) + vec3(0.2, 0.1, 0.05) * aniso * 0.4;
            film *= smoothstep(0.12, wound, rr) * 0.6 + 0.4;
            vec3 through = mix(col * 0.55, film, filmed);       // a window shows film, or what's behind
            vec3 face = mix(flange, through, m);
            // Rim lip and hub.
            float d_disc = rad - r;
            face = bevel(face, d_disc, 0.025);
            float lip = smoothstep(0.035, 0.0, abs(rr - 0.965)) ;
            face += METAL_HI * lip * 0.18 * (0.5 + 0.5 * dot(n, L));
            float hub = 1.0 - smoothstep(0.15, 0.16, rr);
            vec3 chrome = mix(vec3(0.25), vec3(0.85), 0.5 + 0.5 * dot(n, L)) * (1.0 - rr * 2.0);
            face = mix(face, chrome + 0.12, hub);
            float bolt = 1.0 - smoothstep(0.045, 0.055, rr);
            face = mix(face, accent.rgb * 0.9, bolt);
            // Three hub screws turning with the reel.
            for (int i = 0; i < 3; i++) {
                float sa = a + float(i) * 2.0944;
                vec2 sp = c + vec2(cos(sa), sin(sa)) * r * 0.105;
                face = mix(face, vec3(0.05), (1.0 - smoothstep(r * 0.016, r * 0.022, length(p - sp))) * (1.0 - blur * 2.0));
            }
            return mix(col, face, fill(d_disc, px));
        }

        // A film strip along a segment: translucent amber with sprocket holes travelling along it.
        vec3 film_strip(vec2 p, vec2 a, vec2 b, float w, float offset, vec3 col, float px) {
            float h;
            float d = sd_seg(p, a, b, h) - w;
            float cov = fill(d, px);
            if (cov <= 0.0) { return col; }
            vec2 dir = normalize(b - a);
            float s = h * length(b - a);
            float c = dot(p - a, vec2(-dir.y, dir.x));
            vec3 base = vec3(0.32, 0.17, 0.08) + vec3(0.25, 0.14, 0.06) * smoothstep(w, 0.0, abs(c - w * 0.3));
            float edge = smoothstep(w * 0.55, w * 0.62, abs(c)) * (1.0 - smoothstep(w * 0.85, w * 0.9, abs(c)));
            float hole = smoothstep(0.35, 0.4, fract((s - offset) / 0.024)) * (1.0 - smoothstep(0.7, 0.75, fract((s - offset) / 0.024)));
            base = mix(base, col * 0.4 + vec3(0.02), edge * hole);
            return mix(col, base, cov * 0.92);
        }

        // A sprocket wheel with teeth, turning.
        vec3 sprocket(vec2 p, vec2 c, float r, float a, vec3 col, float px) {
            vec2 q = rot(a) * (p - c);
            float th = atan(q.y, q.x);
            float teeth = smoothstep(-0.2, 0.2, cos(th * 14.0));
            float d = length(q) - r - teeth * r * 0.14;
            vec3 base = bevel(METAL * 1.6 + 0.05, d, 0.012);
            base = mix(base, vec3(0.04), 1.0 - smoothstep(r * 0.3, r * 0.36, length(q)));
            return mix(col, base, fill(d, px));
        }

        void fragment() {
            float aspect = size.x / size.y;
            vec2 p = (UV - 0.5) * vec2(aspect, -1.0) * 2.0;
            p = p / zoom + cam;
            float px = 2.0 / size.y / zoom;

            // ---- background: a dark auditorium, red glow at the sides, a floor ----
            vec3 col = vec3(0.012, 0.010, 0.014);
            col += accent.rgb * 0.05 * smoothstep(0.9, 1.7, abs(p.x)) * (0.6 + 0.4 * noise(vec2(p.y * 3.0, t * 0.2)));
            float floor_y = -0.6;
            col += vec3(0.02, 0.016, 0.016) * smoothstep(floor_y + 0.02, floor_y - 0.4, p.y);
            // Contact shadow under the projector.
            float sh = length((p - vec2(-0.18, floor_y)) * vec2(1.0, 7.0));
            col *= mix(0.35, 1.0, smoothstep(0.25, 0.75, sh));

            // ---- beam (behind the projector body, in front of the room) ----
            vec2 lens_o = vec2(0.585, -0.265);
            vec2 bdir = normalize(vec2(1.0, 0.035));
            vec2 bp = p - lens_o;
            float along = dot(bp, bdir);
            float across = abs(bdir.x * bp.y - bdir.y * bp.x);
            float spread = along * 0.27 + 0.045;
            float cone = (1.0 - smoothstep(spread * 0.55, spread, across)) * smoothstep(0.0, 0.05, along);
            float core = 1.0 - smoothstep(0.0, spread * 0.35, across);
            vec2 sp = p * vec2(2.2, 3.0) + vec2(-t * 0.25, t * 0.07);
            float smoke = noise(sp) * 0.6 + noise(sp * 2.7 + 9.0) * 0.4;
            vec3 film_col = 0.5 + 0.5 * cos(6.2831 * (vec3(0.0, 0.33, 0.67) + t * 0.05 + noise(vec2(t * 0.3, 1.0))));
            vec3 tint = vec3(1.0, 0.86, 0.66) * mix(vec3(1.0), 0.75 + 0.35 * film_col, 0.22);
            float flick = 0.94 + 0.06 * noise(vec2(t * 24.0, 3.0));
            float fall = exp(-along * 0.55);
            float beam = (cone * (0.35 + 0.65 * smoke) * 0.42 + core * cone * 0.35) * fall * lamp * flick;
            // Dust motes glinting in the light.
            float dust = 0.0;
            for (int l = 0; l < 3; l++) {
                float fl = float(l);
                float cs = 0.05 + fl * 0.03;
                vec2 q = p + vec2(t * (0.02 + fl * 0.01), -t * (0.015 + fl * 0.008));
                vec2 id = floor(q / cs);
                vec2 o = vec2(hash(id + fl * 17.0), hash(id + 31.0 + fl));
                o += 0.15 * vec2(sin(t * 0.9 + o.y * 30.0), cos(t * 0.8 + o.x * 30.0));
                vec2 cc = (id + 0.15 + o * 0.7) * cs;
                float rr = (0.0016 + fl * 0.0009);
                float mote = exp(-pow(length(q - cc) / rr, 2.0)) * step(0.45, hash(id + 70.0 + fl));
                dust += mote * (0.5 + 0.5 * sin(t * (2.0 + o.x * 4.0) + o.y * 50.0));
            }
            col += tint * (beam + dust * cone * fall * lamp * 1.3);

            // ---- projector ----
            vec2 F = vec2(0.18, 0.30 + (1.0 - drop) * 1.2);    // front reel hub (drops in)
            vec2 R = vec2(-0.43, 0.19);                         // rear reel hub
            float h;
            // Arms up to the reels.
            float d_arm = min(sd_seg(p, vec2(0.10, -0.15), vec2(0.18, 0.30), h) - 0.02, sd_seg(p, vec2(-0.30, -0.15), R, h) - 0.018);
            col = mix(col, bevel(brushed(p, METAL * 1.3, 0.3), d_arm, 0.012), fill(d_arm, px));

            // Film path (behind the reels' faces, over the arms).
            float travel = film_pos;
            col = film_strip(p, vec2(-0.30, -0.12), R + vec2(-0.17, -0.165), 0.017, -travel, col, px);
            col = film_strip(p, F + vec2(0.205, -0.205), vec2(0.16, -0.10), 0.017, travel, col, px);

            // Rear reel (take-up), front reel (feed).
            float blur = clamp(speed * 0.55, 0.0, 0.5);
            col = reel(p, R, 0.25, -ang * 1.25, blur * 1.25, 0.55, col, px);
            col = reel(p, F, 0.30, ang, blur, 0.82, col, px);

            // Lamp house at the back, vents glowing with the lamp.
            vec2 lh = p - vec2(-0.62, -0.25);
            float d_lh = sd_box(lh, vec2(0.13, 0.19), 0.05);
            vec3 lhc = bevel(brushed(p, METAL * 1.1, -0.06), d_lh, 0.02);
            float vent_area = 1.0 - smoothstep(-0.002, 0.002, sd_box(lh - vec2(0.0, 0.02), vec2(0.075, 0.11), 0.01));
            float slot = smoothstep(0.35, 0.45, fract(lh.y * 22.0)) * (1.0 - smoothstep(0.75, 0.85, fract(lh.y * 22.0)));
            vec3 glow = vec3(1.0, 0.45, 0.15) * lamp * (0.6 + 0.4 * noise(vec2(t * 9.0, 2.0)));
            lhc = mix(lhc, vec3(0.03) + glow * 0.9, vent_area * slot);
            col = mix(col, lhc, fill(d_lh, px));

            // Main body.
            vec2 bq = p - vec2(-0.10, -0.31);
            float d_body = sd_box(bq, vec2(0.40, 0.165), 0.035);
            vec3 body = brushed(p, METAL * 1.45, -0.15);
            body += accent.rgb * 0.06 * smoothstep(0.02, 0.0, abs(bq.y + 0.06));   // a red trim line
            body = bevel(body, d_body, 0.03);
            // Panel seam and a small plate.
            body *= 1.0 - 0.35 * (1.0 - smoothstep(0.0, 0.004, abs(bq.x + 0.12))) * step(abs(bq.y), 0.13);
            float plate = 1.0 - smoothstep(-0.002, 0.002, sd_box(bq - vec2(0.2, -0.06), vec2(0.07, 0.022), 0.006));
            body = mix(body, vec3(0.55, 0.45, 0.3) * (0.7 + 0.3 * noise(vec2(p.x * 80.0, 0.0))), plate);
            col = mix(col, body, fill(d_body, px));

            // Sprocket wheels on the face, turning with the film.
            col = sprocket(p, vec2(0.16, -0.20), 0.045, travel * 22.0, col, px);
            col = sprocket(p, vec2(-0.28, -0.20), 0.04, -travel * 25.0, col, px);

            // Base.
            float d_base = sd_box(p - vec2(-0.15, -0.545), vec2(0.5, 0.05), 0.02);
            col = mix(col, bevel(METAL * 0.9, d_base, 0.02), fill(d_base, px));

            // Lens barrel with rings, glass at the end.
            float d_lens = sd_seg(p, vec2(0.28, -0.265), vec2(0.56, -0.265), h) - 0.066;
            float ring = smoothstep(0.3, 0.5, fract(p.x * 38.0)) * 0.12;
            vec3 barrel = METAL * 1.2 * (0.6 + 0.8 * smoothstep(-0.33, -0.22, p.y)) + ring * step(0.33, p.x);
            barrel = bevel(barrel, d_lens, 0.03);
            col = mix(col, barrel, fill(d_lens, px));
            vec2 gq = (p - lens_o) * vec2(1.0 / 0.022, 1.0 / 0.062);
            float d_glass = (length(gq) - 1.0) * 0.022;
            vec3 glass = mix(vec3(0.05, 0.07, 0.09), warm.rgb * 1.6, lamp * (1.0 - smoothstep(0.0, 1.0, length(gq))));
            col = mix(col, glass, fill(d_glass, px));

            // ---- bloom and flare at the lens ----
            float dl = length((p - lens_o) * vec2(0.8, 1.0));
            col += warm.rgb * lamp * (exp(-dl * 16.0) * 0.9 + exp(-dl * 4.0) * 0.12);
            col += warm.rgb * lamp * exp(-abs(p.y - lens_o.y) * 140.0) * exp(-abs(p.x - lens_o.x) * 2.5) * 0.35;

            // ---- grade: vignette and fine grain ----
            vec2 uv = UV - 0.5;
            col *= 1.0 - dot(uv, uv) * 1.1;
            col += (hash(UV * size + fract(t * 13.0) * 100.0) - 0.5) * 0.025;
            COLOR = vec4(max(col, 0.0), alpha);
        }
        """;
}
