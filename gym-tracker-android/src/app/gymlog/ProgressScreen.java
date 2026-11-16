package app.gymlog;

import android.app.Activity;
import android.content.res.ColorStateList;
import android.view.Gravity;
import android.widget.ImageView;
import android.widget.LinearLayout;
import android.widget.TextView;

/** Fills the "Прогресс" tab: overall and per-month share of improved / worse / unchanged sets. */
final class ProgressScreen {
    private ProgressScreen() {}

    static void fill(Activity a, LinearLayout box) {
        box.removeAllViews();
        Progress.Result r = Progress.compute(Db.get(a).allSessions());

        if (r.months.isEmpty()) {
            TextView t = Ui.text(a, "Здесь появится ваш прогресс по месяцам.\n\nЗапишите хотя бы две тренировки "
                    + "на одном тренажёре — тогда появится, с чем сравнивать.", 15, R.color.text_secondary, false);
            t.setLineSpacing(0, 1.15f);
            int p = Ui.dp(a, 8);
            t.setPadding(p, Ui.dp(a, 16), p, p);
            box.addView(t, Ui.matchWrap());
            return;
        }

        LinearLayout total = Ui.card(a);
        total.setTag("progressTotal");
        total.addView(Ui.text(a, "За всё время", 18, R.color.text, true), Ui.matchWrap());
        addCounts(a, total, r.total, null);
        box.addView(total, gap(a, 0));

        for (int i = 0; i < r.months.size(); i++) {
            Progress.Month m = r.months.get(i);
            Progress.Month older = i + 1 < r.months.size() ? r.months.get(i + 1) : null;
            LinearLayout card = Ui.card(a);
            card.setTag("month:" + m.key);
            LinearLayout head = Ui.horizontal(a);
            head.addView(Ui.text(a, Progress.monthTitle(m.key), 18, R.color.text, true), Ui.lpWeight(1));
            head.addView(Ui.text(a, Fmt.workouts(m.days.size()), 14, R.color.text_secondary, false));
            card.addView(head, Ui.matchWrap());
            addCounts(a, card, m.counts, older);

            for (Progress.ExerciseMonth em : m.exercises) {
                LinearLayout row = Ui.vertical(a);
                LinearLayout.LayoutParams rl = Ui.matchWrap();
                rl.topMargin = Ui.dp(a, 10);
                TextView name = Ui.text(a, em.name, 15, R.color.text, true);
                row.addView(name, Ui.matchWrap());
                LinearLayout stats = Ui.horizontal(a);
                if (em.counts.total() > 0) {
                    stat(a, stats, R.drawable.ic_up, R.color.up, em.counts.up);
                    stat(a, stats, R.drawable.ic_down, R.color.down, em.counts.down);
                    stat(a, stats, R.drawable.ic_equal, R.color.equal, em.counts.equal);
                }
                String detail = em.change != null ? em.change
                        : (em.counts.total() == 0 ? "первая запись — сравнивать пока не с чем" : "");
                TextView d = Ui.text(a, detail, 13, em.changePercent == null ? R.color.text_secondary
                        : em.changePercent > 0.5 ? R.color.up : em.changePercent < -0.5 ? R.color.down
                        : R.color.text_secondary, false);
                stats.addView(d, Ui.lpWeight(1));
                LinearLayout.LayoutParams sl = Ui.matchWrap();
                sl.topMargin = Ui.dp(a, 2);
                row.addView(stats, sl);
                card.addView(row, rl);
            }
            box.addView(card, gap(a, 12));
        }

        TextView how = Ui.text(a, "Как считается: каждый подход сравнивается с тем же подходом в прошлую "
                + "тренировку на этом тренажёре — сначала по весу, а если вес тот же, по повторениям "
                + "(как стрелки на экране дня). Процент улучшений = число подходов, ставших лучше, ÷ число "
                + "сравнённых подходов × 100; так же для ухудшений и «без изменений». "
                + "«Макс. вес» — лучший вес на последней тренировке месяца по сравнению с последней "
                + "тренировкой до этого месяца (или с первой в месяце, если раньше записей не было).",
                12, R.color.text_secondary, false);
        how.setLineSpacing(0, 1.1f);
        int hp = Ui.dp(a, 4);
        how.setPadding(hp, 0, hp, 0);
        box.addView(how, gap(a, 16));
    }

    private static LinearLayout.LayoutParams gap(Activity a, int topDp) {
        LinearLayout.LayoutParams lp = Ui.matchWrap();
        lp.topMargin = Ui.dp(a, topDp);
        return lp;
    }

    private static void addCounts(Activity a, LinearLayout card, Progress.Counts c, Progress.Month older) {
        if (c.total() == 0) {
            TextView t = Ui.text(a, "Пока не с чем сравнивать: это первые тренировки на этих тренажёрах.",
                    14, R.color.text_secondary, false);
            LinearLayout.LayoutParams l = Ui.matchWrap();
            l.topMargin = Ui.dp(a, 6);
            card.addView(t, l);
            return;
        }
        int[] pc = c.percents();
        LinearLayout big = Ui.horizontal(a);
        big.setTag("percents");
        bigStat(a, big, pc[0] + "%", "улучшений", R.color.up);
        bigStat(a, big, pc[1] + "%", "ухудшений", R.color.down);
        bigStat(a, big, pc[2] + "%", "без изменений", R.color.equal);
        LinearLayout.LayoutParams bl = Ui.matchWrap();
        bl.topMargin = Ui.dp(a, 10);
        card.addView(big, bl);

        LinearLayout bar = Ui.ratioBar(a, c, 10);
        LinearLayout.LayoutParams rl = Ui.matchWrap();
        rl.topMargin = Ui.dp(a, 10);
        card.addView(bar, rl);

        String sub = "Сравнено подходов: " + c.total() + " (лучше — " + c.up + ", хуже — " + c.down
                + ", так же — " + c.equal + ")";
        if (older != null && older.counts.total() > 0) {
            int prevUp = older.counts.percents()[0];
            int diff = pc[0] - prevUp;
            String monthPrep = Fmt.MONTHS_PREP[Integer.parseInt(older.key.substring(5, 7)) - 1];
            sub += "\n" + (diff > 0 ? "Улучшений больше, чем в " + monthPrep + ": " + pc[0] + "% против " + prevUp + "%."
                    : diff < 0 ? "Улучшений меньше, чем в " + monthPrep + ": " + pc[0] + "% против " + prevUp + "%."
                    : "Доля улучшений такая же, как в " + monthPrep + ": " + pc[0] + "%.");
        }
        TextView s = Ui.text(a, sub, 13, R.color.text_secondary, false);
        s.setLineSpacing(0, 1.1f);
        LinearLayout.LayoutParams sl = Ui.matchWrap();
        sl.topMargin = Ui.dp(a, 8);
        card.addView(s, sl);
    }

    private static void bigStat(Activity a, LinearLayout row, String value, String label, int colorRes) {
        LinearLayout col = Ui.vertical(a);
        col.setGravity(Gravity.CENTER_HORIZONTAL);
        TextView v = Ui.text(a, value, 26, colorRes, true);
        v.setGravity(Gravity.CENTER);
        col.addView(v, Ui.matchWrap());
        TextView l = Ui.text(a, label, 12, R.color.text_secondary, false);
        l.setGravity(Gravity.CENTER);
        col.addView(l, Ui.matchWrap());
        row.addView(col, Ui.lpWeight(1));
    }

    private static void stat(Activity a, LinearLayout row, int icon, int colorRes, int n) {
        ImageView iv = new ImageView(a);
        iv.setImageResource(icon);
        iv.setImageTintList(ColorStateList.valueOf(Ui.color(a, colorRes)));
        row.addView(iv, Ui.lp(Ui.dp(a, 14), Ui.dp(a, 14)));
        TextView t = Ui.text(a, String.valueOf(n), 13, R.color.text, false);
        LinearLayout.LayoutParams l = Ui.lp(LinearLayout.LayoutParams.WRAP_CONTENT, LinearLayout.LayoutParams.WRAP_CONTENT);
        l.leftMargin = Ui.dp(a, 2);
        l.rightMargin = Ui.dp(a, 10);
        row.addView(t, l);
    }
}
