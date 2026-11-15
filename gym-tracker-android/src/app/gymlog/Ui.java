package app.gymlog;

import android.app.Activity;
import android.app.AlertDialog;
import android.content.Context;
import android.content.Intent;
import android.content.res.ColorStateList;
import android.graphics.Typeface;
import android.graphics.drawable.Drawable;
import android.graphics.drawable.GradientDrawable;
import android.graphics.drawable.RippleDrawable;
import android.graphics.drawable.StateListDrawable;
import android.util.TypedValue;
import android.view.Gravity;
import android.view.View;
import android.view.ViewGroup;
import android.widget.Button;
import android.widget.ImageButton;
import android.widget.ImageView;
import android.widget.LinearLayout;
import android.widget.TextView;

import java.util.List;

/** Small helpers for building the UI in code. */
final class Ui {
    private Ui() {}

    static int dp(Context c, float v) {
        return Math.round(v * c.getResources().getDisplayMetrics().density);
    }

    static int color(Context c, int res) {
        return c.getColor(res);
    }

    static GradientDrawable rounded(int fill, float radiusPx) {
        GradientDrawable d = new GradientDrawable();
        d.setColor(fill);
        d.setCornerRadius(radiusPx);
        return d;
    }

    static GradientDrawable rounded(int fill, float radiusPx, int strokeColor, int strokePx) {
        GradientDrawable d = rounded(fill, radiusPx);
        d.setStroke(strokePx, strokeColor);
        return d;
    }

    static GradientDrawable circle(int fill) {
        GradientDrawable d = new GradientDrawable();
        d.setShape(GradientDrawable.OVAL);
        d.setColor(fill);
        return d;
    }

    static Drawable ripple(Context c, Drawable content, Drawable mask) {
        return new RippleDrawable(ColorStateList.valueOf(color(c, R.color.ripple)), content, mask);
    }

    static void selectableBorderless(View v) {
        TypedValue tv = new TypedValue();
        v.getContext().getTheme().resolveAttribute(
                android.R.attr.selectableItemBackgroundBorderless, tv, true);
        v.setBackgroundResource(tv.resourceId);
    }

    static void selectable(View v) {
        TypedValue tv = new TypedValue();
        v.getContext().getTheme().resolveAttribute(android.R.attr.selectableItemBackground, tv, true);
        v.setBackgroundResource(tv.resourceId);
    }

    static TextView text(Context c, CharSequence s, float sp, int colorRes, boolean bold) {
        TextView t = new TextView(c);
        t.setText(s);
        t.setTextSize(TypedValue.COMPLEX_UNIT_SP, sp);
        t.setTextColor(color(c, colorRes));
        if (bold) t.setTypeface(Typeface.create("sans-serif-medium", Typeface.NORMAL));
        return t;
    }

    static LinearLayout.LayoutParams lp(int w, int h) {
        return new LinearLayout.LayoutParams(w, h);
    }

    static LinearLayout.LayoutParams lpWeight(float weight) {
        return new LinearLayout.LayoutParams(0, ViewGroup.LayoutParams.WRAP_CONTENT, weight);
    }

    static LinearLayout.LayoutParams matchWrap() {
        return new LinearLayout.LayoutParams(ViewGroup.LayoutParams.MATCH_PARENT,
                ViewGroup.LayoutParams.WRAP_CONTENT);
    }

    static LinearLayout vertical(Context c) {
        LinearLayout l = new LinearLayout(c);
        l.setOrientation(LinearLayout.VERTICAL);
        return l;
    }

    static LinearLayout horizontal(Context c) {
        LinearLayout l = new LinearLayout(c);
        l.setOrientation(LinearLayout.HORIZONTAL);
        l.setGravity(Gravity.CENTER_VERTICAL);
        return l;
    }

    /** White rounded card. */
    static LinearLayout card(Context c) {
        LinearLayout l = vertical(c);
        l.setBackground(rounded(color(c, R.color.card), dp(c, 16)));
        l.setElevation(dp(c, 1.5f));
        int p = dp(c, 14);
        l.setPadding(p, p, p, p);
        return l;
    }

    static Button primaryButton(Context c, String label) {
        Button b = new Button(c);
        b.setText(label);
        b.setAllCaps(false);
        b.setTextSize(TypedValue.COMPLEX_UNIT_SP, 17);
        b.setTextColor(color(c, R.color.on_accent));
        b.setTypeface(Typeface.create("sans-serif-medium", Typeface.NORMAL));
        float r = dp(c, 14);
        b.setBackground(ripple(c, rounded(color(c, R.color.accent), r), rounded(0xFFFFFFFF, r)));
        b.setStateListAnimator(null);
        b.setMinHeight(dp(c, 54));
        return b;
    }

    static Button softButton(Context c, String label) {
        Button b = new Button(c);
        b.setText(label);
        b.setAllCaps(false);
        b.setTextSize(TypedValue.COMPLEX_UNIT_SP, 16);
        b.setTextColor(color(c, R.color.accent));
        b.setTypeface(Typeface.create("sans-serif-medium", Typeface.NORMAL));
        float r = dp(c, 12);
        b.setBackground(ripple(c, rounded(color(c, R.color.accent_soft), r), rounded(0xFFFFFFFF, r)));
        b.setStateListAnimator(null);
        b.setMinHeight(dp(c, 46));
        return b;
    }

    static ImageButton iconButton(Context c, int iconRes, int tintRes, String description) {
        ImageButton b = new ImageButton(c);
        b.setImageResource(iconRes);
        b.setImageTintList(ColorStateList.valueOf(color(c, tintRes)));
        b.setContentDescription(description);
        selectableBorderless(b);
        int p = dp(c, 8);
        b.setPadding(p, p, p, p);
        return b;
    }

    /** Background for number inputs: highlighted border when focused. */
    static Drawable inputBackground(Context c) {
        float r = dp(c, 10);
        StateListDrawable s = new StateListDrawable();
        s.addState(new int[]{android.R.attr.state_focused},
                rounded(color(c, R.color.input_bg), r, color(c, R.color.accent), dp(c, 2)));
        s.addState(new int[]{},
                rounded(color(c, R.color.input_bg), r, color(c, R.color.divider), dp(c, 1)));
        return s;
    }

    // ---------------------------------------------------------------- comparison indicators

    static int statusIcon(Compare.Status s) {
        switch (s) {
            case UP: return R.drawable.ic_up;
            case DOWN: return R.drawable.ic_down;
            case EQUAL: return R.drawable.ic_equal;
            case MIXED: return R.drawable.ic_mixed;
            case NEW: return R.drawable.ic_new;
            default: return 0;
        }
    }

    static int statusColor(Compare.Status s) {
        switch (s) {
            case UP: return R.color.up;
            case DOWN: return R.color.down;
            case MIXED: return R.color.mixed;
            case NEW: return R.color.fresh;
            default: return R.color.equal;
        }
    }

    static String statusDescription(Compare.Status s) {
        switch (s) {
            case UP: return "Лучше, чем в прошлый раз";
            case DOWN: return "Хуже, чем в прошлый раз";
            case EQUAL: return "Без изменений";
            case MIXED: return "Есть и рост, и спад";
            case NEW: return "Сравнивать не с чем";
            default: return "";
        }
    }

    /**
     * Arrow / "=" icon plus a small "!" icon. Tapping either one opens the explanation.
     * Returns an empty view for {@link Compare.Status#NONE}.
     */
    static View indicator(final Activity a, final Compare.Result r) {
        LinearLayout box = horizontal(a);
        box.setGravity(Gravity.CENTER);
        box.setTag(r.status.name());
        if (r.status == Compare.Status.NONE) return box;
        int col = color(a, statusColor(r.status));

        ImageView arrow = new ImageView(a);
        arrow.setImageResource(statusIcon(r.status));
        arrow.setImageTintList(ColorStateList.valueOf(col));
        box.addView(arrow, lp(dp(a, 24), dp(a, 24)));

        ImageView info = new ImageView(a);
        info.setImageResource(R.drawable.ic_info);
        info.setImageTintList(ColorStateList.valueOf(col));
        info.setAlpha(0.85f);
        LinearLayout.LayoutParams ilp = lp(dp(a, 16), dp(a, 16));
        ilp.leftMargin = dp(a, 2);
        box.addView(info, ilp);

        int p = dp(a, 5);
        box.setPadding(p, p, p, p);
        selectableBorderless(box);
        box.setClickable(true);
        box.setContentDescription(statusDescription(r.status) + ". Нажмите, чтобы узнать почему");
        box.setOnClickListener(new View.OnClickListener() {
            @Override
            public void onClick(View v) {
                showResult(a, r);
            }
        });
        return box;
    }

    static AlertDialog showResult(Activity a, Compare.Result r) {
        Drawable icon = a.getDrawable(statusIcon(r.status));
        if (icon != null) {
            icon = icon.mutate();
            icon.setTint(color(a, statusColor(r.status)));
        }
        return new AlertDialog.Builder(a)
                .setIcon(icon)
                .setTitle(r.title)
                .setMessage(r.message)
                .setPositiveButton("Понятно", null)
                .show();
    }

    // ---------------------------------------------------------------- shared dialogs

    static void showHistory(final Activity a, long exerciseId, String name, final String currentDay) {
        final List<Model.Session> h = Db.get(a).history(exerciseId);
        AlertDialog.Builder b = new AlertDialog.Builder(a).setTitle("История: " + name)
                .setPositiveButton("Закрыть", null);
        if (h.isEmpty()) {
            b.setMessage("Пока нет записанных подходов.");
        } else {
            String[] items = new String[h.size()];
            for (int i = 0; i < h.size(); i++) {
                Model.Session s = h.get(i);
                items[i] = Fmt.dateLong(s.day) + "\n" + Fmt.joinSets(s.sets);
            }
            b.setItems(items, new android.content.DialogInterface.OnClickListener() {
                @Override
                public void onClick(android.content.DialogInterface d, int which) {
                    String day = h.get(which).day;
                    if (!day.equals(currentDay)) openDay(a, day);
                }
            });
        }
        b.show();
    }

    static void openDay(Activity a, String day) {
        a.startActivity(new Intent(a, DayActivity.class).putExtra(DayActivity.EXTRA_DAY, day));
    }
}
