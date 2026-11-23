package app.gymlog;

import android.content.Context;

import org.json.JSONArray;
import org.json.JSONObject;

import java.io.ByteArrayOutputStream;
import java.io.InputStream;
import java.nio.charset.StandardCharsets;

/**
 * Optional personal catalog: assets/extra/catalog.json with photos in assets/extra/img/.
 * It exists only in a personal build (made by tools/atletiq_personal.py); the public build has none.
 */
final class Extras {
    private Extras() {}

    private static boolean sLoaded;
    static int sCount;
    static String sSource;

    static synchronized void load(Context c) {
        if (sLoaded) return;
        sLoaded = true;
        InputStream in = null;
        try {
            in = c.getAssets().open("extra/catalog.json");
            sCount = loadFrom(in);
        } catch (java.io.FileNotFoundException e) {
            // public build: no personal catalog
        } catch (Exception e) {
            android.util.Log.w("gymlog", "personal catalog not loaded", e);
        } finally {
            if (in != null) {
                try {
                    in.close();
                } catch (java.io.IOException ignored) {
                    // nothing to do
                }
            }
        }
    }

    /** Adds the exercises of a catalog.json stream to the reference; returns how many were added. */
    static int loadFrom(InputStream in) throws Exception {
        int added = 0;
        {
            ByteArrayOutputStream bos = new ByteArrayOutputStream();
            byte[] buf = new byte[16384];
            int n;
            while ((n = in.read(buf)) > 0) bos.write(buf, 0, n);
            JSONObject root = new JSONObject(new String(bos.toByteArray(), StandardCharsets.UTF_8));
            sSource = root.optString("source", null);
            JSONArray list = root.getJSONArray("exercises");
            for (int i = 0; i < list.length(); i++) {
                JSONObject o = list.getJSONObject(i);
                JSONArray g = o.getJSONArray("groups");
                String[] groups = new String[g.length()];
                for (int k = 0; k < groups.length; k++) groups[k] = g.getString(k);
                JSONArray st = o.optJSONArray("steps");
                String[] steps = new String[st == null ? 0 : st.length()];
                for (int k = 0; k < steps.length; k++) steps[k] = st.getString(k);
                String id = o.getString("id");
                if (Catalog.addExtraItem("a_" + id, o.getString("name"), o.optString("muscles", ""), groups,
                        o.optBoolean("bodyweight", false), o.optString("equipment", "other"),
                        o.isNull("equipmentTitle") ? null : o.optString("equipmentTitle", null), steps,
                        o.isNull("video") ? null : o.optString("video", null), id, o.optInt("frames", 1),
                        o.isNull("url") ? null : o.optString("url", null))) {
                    added++;
                }
            }
        }
        return added;
    }
}
