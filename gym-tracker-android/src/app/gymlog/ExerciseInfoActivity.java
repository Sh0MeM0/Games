package app.gymlog;

import android.app.ActionBar;
import android.content.ActivityNotFoundException;
import android.content.Intent;
import android.net.Uri;
import android.os.Bundle;
import android.view.Gravity;
import android.view.View;
import android.view.ViewGroup;
import android.widget.Button;
import android.widget.FrameLayout;
import android.widget.LinearLayout;
import android.widget.ScrollView;
import android.widget.TextView;
import android.widget.Toast;

import java.io.UnsupportedEncodingException;
import java.net.URLEncoder;

/** Additional information about a machine: picture, muscles, technique, typical mistakes, video search. */
public class ExerciseInfoActivity extends BaseActivity {
    public static final String EXTRA_EXERCISE = "exercise";

    private long exerciseId;
    private LinearLayout content;

    @Override
    protected void onCreate(Bundle saved) {
        super.onCreate(saved);
        exerciseId = getIntent().getLongExtra(EXTRA_EXERCISE, -1);
        ActionBar ab = getActionBar();
        if (ab != null) ab.setDisplayHomeAsUpEnabled(true);
        ScrollView scroll = new ScrollView(this);
        content = Ui.vertical(this);
        int p = Ui.dp(this, 16);
        content.setPadding(p, p, p, Ui.dp(this, 28));
        scroll.addView(content, new ScrollView.LayoutParams(ViewGroup.LayoutParams.MATCH_PARENT,
                ViewGroup.LayoutParams.WRAP_CONTENT));
        setContentView(scroll);
        reload();
    }

    void reload() {
        content.removeAllViews();
        Model.Exercise ex = Db.get(this).exercise(exerciseId);
        if (ex == null) {
            finish();
            return;
        }
        setTitle(ex.name);
        final Catalog.Item item = Catalog.get(ex.catalog);
        ActionBar ab = getActionBar();
        if (ab != null) ab.setSubtitle(item != null && !Fmt.normName(item.title).equals(Fmt.normName(ex.name))
                ? item.title : null);

        LinearLayout top = Ui.card(this);
        top.setGravity(Gravity.CENTER_HORIZONTAL);
        View.OnClickListener pick = new View.OnClickListener() {
            @Override
            public void onClick(View v) {
                pickPicture();
            }
        };
        if (Ui.hasPhoto(ex.catalog)) {
            // Start and end of the movement, side by side.
            LinearLayout photos = Ui.horizontal(this);
            for (int f = 0; f < 2; f++) {
                LinearLayout col = Ui.vertical(this);
                FrameLayout ph = Ui.photoBox(this, ex.catalog, f, 48, Ui.dp(this, 12));
                ph.setTag("photo:" + f);
                ph.setOnClickListener(pick);
                col.addView(ph, new LinearLayout.LayoutParams(ViewGroup.LayoutParams.MATCH_PARENT, Ui.dp(this, 130)));
                TextView cap = Ui.text(this, f == 0 ? "Начало" : "Конец", 12, R.color.text_secondary, false);
                cap.setGravity(Gravity.CENTER);
                col.addView(cap, Ui.matchWrap());
                LinearLayout.LayoutParams lp = Ui.lpWeight(1);
                if (f == 0) lp.rightMargin = Ui.dp(this, 4); else lp.leftMargin = Ui.dp(this, 4);
                photos.addView(col, lp);
            }
            top.addView(photos, Ui.matchWrap());
        } else {
            FrameLayout pic = Ui.thumbnail(this, ex.catalog, 120);
            pic.setOnClickListener(pick);
            top.addView(pic, Ui.lp(Ui.dp(this, 120), Ui.dp(this, 120)));
        }
        String hint = item == null
                ? "Фото не выбрано. Нажмите на значок выше и выберите упражнение из справочника."
                : (ex.catalogManual ? "Фото выбрано вами." : "Фото подобрано автоматически по названию.")
                  + " Если не подходит — нажмите на него и выберите другое.";
        TextView h = Ui.text(this, hint, 13, R.color.text_secondary, false);
        h.setGravity(Gravity.CENTER);
        LinearLayout.LayoutParams hl = Ui.matchWrap();
        hl.topMargin = Ui.dp(this, 8);
        top.addView(h, hl);
        content.addView(top, Ui.matchWrap());

        if (item == null) return;

        if (!item.about.isEmpty()) section("Что это", item.about);
        section("Какие мышцы работают", Fmt.capitalize(item.muscles) + ".");
        section("Оборудование", Catalog.equipmentTitle(item.equipment()) + ".");
        if (Db.isExtra(ex.catalog, ex.weightMode)) {
            section("Как записывать", "Здесь работает собственный вес, поэтому графа веса — «Доп. вес»: "
                    + "пишите только утяжеление (например, 10 — это +10 кг) или оставляйте пустым. "
                    + "Переключить можно в меню ⋮ у тренажёра на экране дня.");
        }
        StringBuilder tech = new StringBuilder();
        for (int i = 0; i < item.technique.length; i++) {
            if (i > 0) tech.append("\n\n");
            tech.append(i + 1).append(". ").append(item.technique[i]);
        }
        if (item.technique.length > 0) {
            section("Техника", tech.toString());
        } else {
            section("Техника", "Подробного описания техники для этого упражнения в приложении нет: "
                    + "смотрите фото начала и конца движения выше и видео по кнопке ниже.");
        }
        StringBuilder mist = new StringBuilder();
        for (int i = 0; i < item.mistakes.length; i++) {
            if (i > 0) mist.append('\n');
            mist.append("•  ").append(item.mistakes[i]);
        }
        if (item.mistakes.length > 0) section("Частые ошибки", mist.toString());

        Button video = Ui.softButton(this, "Найти видео с техникой на YouTube");
        video.setTag("video");
        video.setOnClickListener(new View.OnClickListener() {
            @Override
            public void onClick(View v) {
                openVideoSearch(item);
            }
        });
        LinearLayout.LayoutParams vl = Ui.matchWrap();
        vl.topMargin = Ui.dp(this, 14);
        content.addView(video, vl);

        TextView note = Ui.text(this, "Это общие рекомендации, а не консультация врача или тренера. "
                + "При травмах, боли или заболеваниях обратитесь к специалисту."
                + (item.photo() != null ? "\n\nФото: Free Exercise DB (github.com/yuhonas/free-exercise-db), "
                + "опубликовано как общественное достояние (Unlicense)." : ""), 12, R.color.text_secondary, false);
        note.setTag("attribution");
        LinearLayout.LayoutParams nl = Ui.matchWrap();
        nl.topMargin = Ui.dp(this, 14);
        content.addView(note, nl);
    }

    private void section(String title, String body) {
        LinearLayout card = Ui.card(this);
        card.addView(Ui.text(this, title, 17, R.color.text, true), Ui.matchWrap());
        TextView b = Ui.text(this, body, 15, R.color.text, false);
        b.setLineSpacing(0, 1.15f);
        LinearLayout.LayoutParams bl = Ui.matchWrap();
        bl.topMargin = Ui.dp(this, 6);
        card.addView(b, bl);
        LinearLayout.LayoutParams cl = Ui.matchWrap();
        cl.topMargin = Ui.dp(this, 12);
        content.addView(card, cl);
    }

    void pickPicture() {
        Ui.showPicker(this, exerciseId, new Runnable() {
            @Override
            public void run() {
                reload();
            }
        });
    }

    static Uri videoSearchUri(Catalog.Item item) {
        try {
            return Uri.parse("https://www.youtube.com/results?search_query="
                    + URLEncoder.encode(item.videoQuery(), "UTF-8"));
        } catch (UnsupportedEncodingException e) {
            throw new IllegalStateException(e);
        }
    }

    private void openVideoSearch(Catalog.Item item) {
        try {
            startActivity(new Intent(Intent.ACTION_VIEW, videoSearchUri(item)));
        } catch (ActivityNotFoundException e) {
            Toast.makeText(this, "Не найдено приложение для открытия ссылки", Toast.LENGTH_LONG).show();
        }
    }
}
