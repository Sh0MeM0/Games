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
        FrameLayout pic = Ui.thumbnail(this, ex.catalog, 200);
        pic.setOnClickListener(new View.OnClickListener() {
            @Override
            public void onClick(View v) {
                pickPicture();
            }
        });
        top.addView(pic, Ui.lp(Ui.dp(this, 200), Ui.dp(this, 200)));
        String hint = item == null
                ? "Картинка не выбрана. Нажмите на значок выше, чтобы выбрать подходящий тренажёр из справочника."
                : (ex.catalogManual ? "Картинка выбрана вами." : "Картинка подобрана автоматически по названию.")
                  + " Если не подходит — нажмите на неё и выберите другую.";
        TextView h = Ui.text(this, hint, 13, R.color.text_secondary, false);
        h.setGravity(Gravity.CENTER);
        LinearLayout.LayoutParams hl = Ui.matchWrap();
        hl.topMargin = Ui.dp(this, 8);
        top.addView(h, hl);
        content.addView(top, Ui.matchWrap());

        if (item == null) return;

        section("Что это", item.about);
        section("Какие мышцы работают", Fmt.capitalize(item.muscles) + ".");
        StringBuilder tech = new StringBuilder();
        for (int i = 0; i < item.technique.length; i++) {
            if (i > 0) tech.append("\n\n");
            tech.append(i + 1).append(". ").append(item.technique[i]);
        }
        section("Техника", tech.toString());
        StringBuilder mist = new StringBuilder();
        for (int i = 0; i < item.mistakes.length; i++) {
            if (i > 0) mist.append('\n');
            mist.append("•  ").append(item.mistakes[i]);
        }
        section("Частые ошибки", mist.toString());

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
                + "При травмах, боли или заболеваниях обратитесь к специалисту.", 12, R.color.text_secondary, false);
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
