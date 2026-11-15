package app.gymlog;

import android.app.Activity;
import android.app.AlertDialog;
import android.content.ActivityNotFoundException;
import android.content.DialogInterface;
import android.content.Intent;
import android.net.Uri;
import android.os.Bundle;
import android.view.Gravity;
import android.view.Menu;
import android.view.MenuItem;
import android.view.View;
import android.view.ViewGroup;
import android.widget.Button;
import android.widget.FrameLayout;
import android.widget.ImageButton;
import android.widget.LinearLayout;
import android.widget.ScrollView;
import android.widget.TextView;
import android.widget.Toast;

import org.json.JSONObject;

import java.io.ByteArrayOutputStream;
import java.io.InputStream;
import java.io.OutputStream;
import java.nio.charset.StandardCharsets;
import java.time.LocalDate;
import java.time.YearMonth;
import java.util.List;
import java.util.Set;

/** Month calendar. Tap a date to open that day's workout. */
public class MainActivity extends Activity {
    static final int REQ_EXPORT = 11;
    static final int REQ_IMPORT = 12;
    private static final int MENU_EXERCISES = 1;
    private static final int MENU_EXPORT = 2;
    private static final int MENU_IMPORT = 3;

    private YearMonth month;
    private TextView monthTitle;
    private TextView monthStats;
    private LinearLayout grid;
    private LinearLayout recentBox;

    @Override
    protected void onCreate(Bundle saved) {
        super.onCreate(saved);
        month = YearMonth.now();
        if (saved != null && saved.getString("month") != null) {
            month = YearMonth.parse(saved.getString("month"));
        }
        setTitle(R.string.app_name);
        buildUi();
    }

    @Override
    protected void onResume() {
        super.onResume();
        refresh();
    }

    @Override
    protected void onSaveInstanceState(Bundle out) {
        super.onSaveInstanceState(out);
        out.putString("month", month.toString());
    }

    private void buildUi() {
        ScrollView scroll = new ScrollView(this);
        scroll.setFillViewport(true);
        LinearLayout root = Ui.vertical(this);
        int p = Ui.dp(this, 12);
        root.setPadding(p, p, p, p);
        scroll.addView(root, new ScrollView.LayoutParams(ViewGroup.LayoutParams.MATCH_PARENT,
                ViewGroup.LayoutParams.WRAP_CONTENT));

        // Calendar card
        LinearLayout card = Ui.card(this);
        LinearLayout header = Ui.horizontal(this);
        ImageButton prev = Ui.iconButton(this, R.drawable.ic_chevron_left, R.color.text, "Предыдущий месяц");
        prev.setOnClickListener(new View.OnClickListener() {
            @Override
            public void onClick(View v) {
                month = month.minusMonths(1);
                refresh();
            }
        });
        ImageButton next = Ui.iconButton(this, R.drawable.ic_chevron_right, R.color.text, "Следующий месяц");
        next.setOnClickListener(new View.OnClickListener() {
            @Override
            public void onClick(View v) {
                month = month.plusMonths(1);
                refresh();
            }
        });
        monthTitle = Ui.text(this, "", 20, R.color.text, true);
        monthTitle.setGravity(Gravity.CENTER);
        monthTitle.setOnClickListener(new View.OnClickListener() {
            @Override
            public void onClick(View v) {
                month = YearMonth.now();
                refresh();
            }
        });
        header.addView(prev, Ui.lp(Ui.dp(this, 44), Ui.dp(this, 44)));
        header.addView(monthTitle, Ui.lpWeight(1));
        header.addView(next, Ui.lp(Ui.dp(this, 44), Ui.dp(this, 44)));
        card.addView(header, Ui.matchWrap());

        LinearLayout weekdays = Ui.horizontal(this);
        for (int i = 0; i < 7; i++) {
            TextView t = Ui.text(this, Fmt.WEEKDAYS_SHORT[i], 13,
                    i >= 5 ? R.color.down : R.color.text_secondary, false);
            t.setGravity(Gravity.CENTER);
            weekdays.addView(t, Ui.lpWeight(1));
        }
        LinearLayout.LayoutParams wlp = Ui.matchWrap();
        wlp.topMargin = Ui.dp(this, 8);
        wlp.bottomMargin = Ui.dp(this, 4);
        card.addView(weekdays, wlp);

        grid = Ui.vertical(this);
        card.addView(grid, Ui.matchWrap());
        root.addView(card, Ui.matchWrap());

        monthStats = Ui.text(this, "", 14, R.color.text_secondary, false);
        monthStats.setGravity(Gravity.CENTER);
        LinearLayout.LayoutParams slp = Ui.matchWrap();
        slp.topMargin = Ui.dp(this, 10);
        root.addView(monthStats, slp);

        Button today = Ui.primaryButton(this, "Тренировка сегодня");
        today.setOnClickListener(new View.OnClickListener() {
            @Override
            public void onClick(View v) {
                Ui.openDay(MainActivity.this, LocalDate.now().toString());
            }
        });
        LinearLayout.LayoutParams tlp = Ui.matchWrap();
        tlp.topMargin = Ui.dp(this, 14);
        root.addView(today, tlp);

        TextView recentTitle = Ui.text(this, "Последние тренировки", 18, R.color.text, true);
        LinearLayout.LayoutParams rlp = Ui.matchWrap();
        rlp.topMargin = Ui.dp(this, 22);
        rlp.bottomMargin = Ui.dp(this, 8);
        root.addView(recentTitle, rlp);

        recentBox = Ui.vertical(this);
        root.addView(recentBox, Ui.matchWrap());

        setContentView(scroll);
    }

    void refresh() {
        monthTitle.setText(Fmt.monthTitle(month.getYear(), month.getMonthValue()));
        LocalDate first = month.atDay(1);
        LocalDate last = month.atEndOfMonth();
        Db db = Db.get(this);
        Set<String> days = db.daysWithEntries(first.toString(), last.toString());
        String todayIso = LocalDate.now().toString();

        grid.removeAllViews();
        int offset = first.getDayOfWeek().getValue() - 1; // Monday = 0
        int length = month.lengthOfMonth();
        int cellH = Ui.dp(this, 44);
        int circle = Ui.dp(this, 38);
        for (int r = 0; r < 6; r++) {
            LinearLayout row = Ui.horizontal(this);
            for (int c = 0; c < 7; c++) {
                int dayNum = r * 7 + c - offset + 1;
                FrameLayout cell = new FrameLayout(this);
                if (dayNum >= 1 && dayNum <= length) {
                    final String iso = month.atDay(dayNum).toString();
                    boolean has = days.contains(iso);
                    boolean isToday = iso.equals(todayIso);
                    TextView t = Ui.text(this, String.valueOf(dayNum), 16,
                            has ? R.color.on_accent : (isToday ? R.color.accent : R.color.text),
                            has || isToday);
                    t.setGravity(Gravity.CENTER);
                    if (has) {
                        android.graphics.drawable.GradientDrawable d =
                                Ui.circle(Ui.color(this, R.color.accent));
                        if (isToday) d.setStroke(Ui.dp(this, 3), Ui.color(this, R.color.up));
                        t.setBackground(d);
                    } else if (isToday) {
                        android.graphics.drawable.GradientDrawable d =
                                Ui.circle(Ui.color(this, R.color.accent_soft));
                        d.setStroke(Ui.dp(this, 2), Ui.color(this, R.color.accent));
                        t.setBackground(d);
                    }
                    t.setTag("day:" + iso);
                    t.setContentDescription(Fmt.dateLong(iso) + (has ? ", есть тренировка" : ""));
                    t.setOnClickListener(new View.OnClickListener() {
                        @Override
                        public void onClick(View v) {
                            Ui.openDay(MainActivity.this, iso);
                        }
                    });
                    t.setForeground(Ui.ripple(this, null, Ui.circle(0xFFFFFFFF)));
                    cell.addView(t, new FrameLayout.LayoutParams(circle, circle, Gravity.CENTER));
                }
                row.addView(cell, new LinearLayout.LayoutParams(0, cellH, 1));
            }
            grid.addView(row, Ui.matchWrap());
        }

        int count = days.size();
        monthStats.setText(count == 0
                ? "В " + Fmt.MONTHS_PREP[month.getMonthValue() - 1] + " тренировок пока нет"
                : "Тренировок в " + Fmt.MONTHS_PREP[month.getMonthValue() - 1] + ": " + count);

        recentBox.removeAllViews();
        List<String> recent = db.recentDays(7);
        if (recent.isEmpty()) {
            TextView t = Ui.text(this, "Здесь появятся ваши тренировки.\nВыберите дату в календаре "
                    + "или нажмите «Тренировка сегодня», чтобы начать.", 15, R.color.text_secondary, false);
            t.setLineSpacing(0, 1.15f);
            recentBox.addView(t, Ui.matchWrap());
        }
        for (final String day : recent) {
            LinearLayout item = Ui.card(this);
            item.setElevation(Ui.dp(this, 1));
            String title = Fmt.dateLong(day) + ", " + Fmt.weekday(day);
            if (day.equals(todayIso)) title += " · сегодня";
            item.addView(Ui.text(this, title, 16, R.color.text, true), Ui.matchWrap());
            List<String> names = db.exerciseNamesForDay(day);
            StringBuilder sb = new StringBuilder();
            for (String n : names) {
                if (sb.length() > 0) sb.append(", ");
                sb.append(n);
            }
            TextView sub = Ui.text(this, sb.toString(), 14, R.color.text_secondary, false);
            LinearLayout.LayoutParams sl = Ui.matchWrap();
            sl.topMargin = Ui.dp(this, 3);
            item.addView(sub, sl);
            item.setForeground(Ui.ripple(this, null, Ui.rounded(0xFFFFFFFF, Ui.dp(this, 16))));
            item.setClickable(true);
            item.setOnClickListener(new View.OnClickListener() {
                @Override
                public void onClick(View v) {
                    Ui.openDay(MainActivity.this, day);
                }
            });
            LinearLayout.LayoutParams ilp = Ui.matchWrap();
            ilp.bottomMargin = Ui.dp(this, 10);
            recentBox.addView(item, ilp);
        }
    }

    YearMonth shownMonth() {
        return month;
    }

    // ---------------------------------------------------------------- menu: exercises, backup

    @Override
    public boolean onCreateOptionsMenu(Menu menu) {
        menu.add(0, MENU_EXERCISES, 0, "Мои тренажёры");
        menu.add(0, MENU_EXPORT, 1, "Сохранить резервную копию");
        menu.add(0, MENU_IMPORT, 2, "Восстановить из копии");
        return true;
    }

    @Override
    public boolean onOptionsItemSelected(MenuItem item) {
        switch (item.getItemId()) {
            case MENU_EXERCISES:
                startActivity(new Intent(this, ExercisesActivity.class));
                return true;
            case MENU_EXPORT: {
                Intent i = new Intent(Intent.ACTION_CREATE_DOCUMENT);
                i.addCategory(Intent.CATEGORY_OPENABLE);
                i.setType("application/json");
                i.putExtra(Intent.EXTRA_TITLE, "dnevnik-zala-" + LocalDate.now() + ".json");
                startForResult(i, REQ_EXPORT);
                return true;
            }
            case MENU_IMPORT: {
                Intent i = new Intent(Intent.ACTION_OPEN_DOCUMENT);
                i.addCategory(Intent.CATEGORY_OPENABLE);
                i.setType("*/*");
                startForResult(i, REQ_IMPORT);
                return true;
            }
            default:
                return super.onOptionsItemSelected(item);
        }
    }

    private void startForResult(Intent i, int req) {
        try {
            startActivityForResult(i, req);
        } catch (ActivityNotFoundException e) {
            Toast.makeText(this, "На телефоне нет приложения для выбора файлов", Toast.LENGTH_LONG).show();
        }
    }

    @Override
    protected void onActivityResult(int requestCode, int resultCode, Intent data) {
        super.onActivityResult(requestCode, resultCode, data);
        if (resultCode != RESULT_OK || data == null || data.getData() == null) return;
        Uri uri = data.getData();
        if (requestCode == REQ_EXPORT) exportTo(uri);
        else if (requestCode == REQ_IMPORT) importFrom(uri);
    }

    private void exportTo(Uri uri) {
        try {
            byte[] bytes = Db.get(this).exportJson().toString(1).getBytes(StandardCharsets.UTF_8);
            OutputStream os = getContentResolver().openOutputStream(uri, "wt");
            if (os == null) throw new java.io.IOException("no stream");
            try {
                os.write(bytes);
            } finally {
                os.close();
            }
            Toast.makeText(this, "Резервная копия сохранена", Toast.LENGTH_LONG).show();
        } catch (Exception e) {
            Toast.makeText(this, "Не удалось сохранить копию: " + e.getMessage(), Toast.LENGTH_LONG).show();
        }
    }

    private void importFrom(Uri uri) {
        final JSONObject root;
        final int[] counts;
        try {
            InputStream is = getContentResolver().openInputStream(uri);
            if (is == null) throw new java.io.IOException("no stream");
            ByteArrayOutputStream bos = new ByteArrayOutputStream();
            try {
                byte[] buf = new byte[8192];
                int n;
                while ((n = is.read(buf)) > 0) bos.write(buf, 0, n);
            } finally {
                is.close();
            }
            root = new JSONObject(new String(bos.toByteArray(), StandardCharsets.UTF_8));
            counts = Db.inspectBackup(root);
        } catch (Exception e) {
            Toast.makeText(this, "Это не файл резервной копии дневника", Toast.LENGTH_LONG).show();
            return;
        }
        new AlertDialog.Builder(this)
                .setTitle("Восстановить данные?")
                .setMessage("В копии: тренажёров — " + counts[0] + ", дней с тренировками — "
                        + counts[1] + ".\n\nТекущие данные в приложении будут заменены данными из файла.")
                .setNegativeButton("Отмена", null)
                .setPositiveButton("Восстановить", new DialogInterface.OnClickListener() {
                    @Override
                    public void onClick(DialogInterface d, int which) {
                        try {
                            Db.get(MainActivity.this).importJson(root);
                            refresh();
                            Toast.makeText(MainActivity.this, "Данные восстановлены", Toast.LENGTH_LONG).show();
                        } catch (Exception e) {
                            Toast.makeText(MainActivity.this, "Ошибка восстановления: " + e.getMessage(),
                                    Toast.LENGTH_LONG).show();
                        }
                    }
                })
                .show();
    }
}
