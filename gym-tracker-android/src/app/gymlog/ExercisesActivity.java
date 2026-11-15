package app.gymlog;

import android.app.ActionBar;
import android.app.Activity;
import android.app.AlertDialog;
import android.content.DialogInterface;
import android.os.Bundle;
import android.text.InputFilter;
import android.text.InputType;
import android.view.MenuItem;
import android.view.View;
import android.view.ViewGroup;
import android.widget.EditText;
import android.widget.FrameLayout;
import android.widget.LinearLayout;
import android.widget.ScrollView;
import android.widget.TextView;
import android.widget.Toast;

import java.util.List;

/** List of saved machines: history, rename, delete. */
public class ExercisesActivity extends Activity {
    private LinearLayout list;

    @Override
    protected void onCreate(Bundle saved) {
        super.onCreate(saved);
        setTitle("Мои тренажёры");
        ActionBar ab = getActionBar();
        if (ab != null) ab.setDisplayHomeAsUpEnabled(true);
        ScrollView scroll = new ScrollView(this);
        list = Ui.vertical(this);
        int p = Ui.dp(this, 12);
        list.setPadding(p, p, p, p);
        scroll.addView(list, new ScrollView.LayoutParams(ViewGroup.LayoutParams.MATCH_PARENT,
                ViewGroup.LayoutParams.WRAP_CONTENT));
        setContentView(scroll);
    }

    @Override
    protected void onResume() {
        super.onResume();
        reload();
    }

    @Override
    public boolean onOptionsItemSelected(MenuItem item) {
        if (item.getItemId() == android.R.id.home) {
            finish();
            return true;
        }
        return super.onOptionsItemSelected(item);
    }

    void reload() {
        list.removeAllViews();
        List<Model.Exercise> all = Db.get(this).exercises();
        if (all.isEmpty()) {
            TextView t = Ui.text(this, "Сохранённых тренажёров пока нет.\n\nОни появятся здесь, "
                    + "когда вы добавите тренажёр в какой-нибудь день.", 15, R.color.text_secondary, false);
            int ep = Ui.dp(this, 16);
            t.setPadding(ep, ep, ep, ep);
            list.addView(t, Ui.matchWrap());
            return;
        }
        for (final Model.Exercise ex : all) {
            LinearLayout item = Ui.card(this);
            item.addView(Ui.text(this, ex.name, 17, R.color.text, true), Ui.matchWrap());
            String info = ex.lastDay == null
                    ? "Ещё не записан ни в один день"
                    : Fmt.workouts(ex.sessions) + " · последняя: " + Fmt.dateLong(ex.lastDay);
            TextView sub = Ui.text(this, info, 14, R.color.text_secondary, false);
            LinearLayout.LayoutParams sl = Ui.matchWrap();
            sl.topMargin = Ui.dp(this, 3);
            item.addView(sub, sl);
            item.setForeground(Ui.ripple(this, null, Ui.rounded(0xFFFFFFFF, Ui.dp(this, 16))));
            item.setClickable(true);
            item.setOnClickListener(new View.OnClickListener() {
                @Override
                public void onClick(View v) {
                    showOptions(ex);
                }
            });
            LinearLayout.LayoutParams ilp = Ui.matchWrap();
            ilp.bottomMargin = Ui.dp(this, 10);
            list.addView(item, ilp);
        }
    }

    private void showOptions(final Model.Exercise ex) {
        new AlertDialog.Builder(this)
                .setTitle(ex.name)
                .setItems(new String[]{"История", "Переименовать", "Удалить"},
                        new DialogInterface.OnClickListener() {
                            @Override
                            public void onClick(DialogInterface d, int which) {
                                if (which == 0) Ui.showHistory(ExercisesActivity.this, ex.id, ex.name, null);
                                else if (which == 1) rename(ex);
                                else confirmDelete(ex);
                            }
                        })
                .show();
    }

    private void rename(final Model.Exercise ex) {
        final EditText input = new EditText(this);
        input.setSingleLine(true);
        input.setInputType(InputType.TYPE_CLASS_TEXT | InputType.TYPE_TEXT_FLAG_CAP_SENTENCES);
        input.setFilters(new InputFilter[]{new InputFilter.LengthFilter(60)});
        input.setText(ex.name);
        input.setSelection(input.getText().length());
        FrameLayout box = new FrameLayout(this);
        int p = Ui.dp(this, 20);
        box.setPadding(p, Ui.dp(this, 8), p, 0);
        box.addView(input);
        new AlertDialog.Builder(this)
                .setTitle("Переименовать")
                .setView(box)
                .setNegativeButton("Отмена", null)
                .setPositiveButton("Сохранить", new DialogInterface.OnClickListener() {
                    @Override
                    public void onClick(DialogInterface d, int which) {
                        if (!Db.get(ExercisesActivity.this).renameExercise(ex.id, input.getText().toString())) {
                            Toast.makeText(ExercisesActivity.this,
                                    "Такое название уже есть или оно пустое", Toast.LENGTH_LONG).show();
                        }
                        reload();
                    }
                })
                .show();
    }

    private void confirmDelete(final Model.Exercise ex) {
        String msg = ex.sessions > 0
                ? "Будет удалена вся история этого тренажёра (" + Fmt.workouts(ex.sessions)
                  + "). Это нельзя отменить."
                : "Тренажёр будет удалён из списка.";
        new AlertDialog.Builder(this)
                .setTitle("Удалить «" + ex.name + "»?")
                .setMessage(msg)
                .setNegativeButton("Отмена", null)
                .setPositiveButton("Удалить", new DialogInterface.OnClickListener() {
                    @Override
                    public void onClick(DialogInterface d, int which) {
                        Db.get(ExercisesActivity.this).deleteExercise(ex.id);
                        reload();
                    }
                })
                .show();
    }
}
