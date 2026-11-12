;;; Test data for stage 1 of the "Kartogramma" module (KZM_IMPORT).
;;; ASCII only on purpose: loads the same way with any LISPSYS setting.
;;; Usage: APPLOAD -> this file -> command KZM_TEST_DATA in an empty drawing.
;;; Expected results are listed in VALIDATION.md, section "Test data".

(defun kzm-test:layer (name color)
  (if (not (tblsearch "LAYER" name))
    (entmake (list '(0 . "LAYER") '(100 . "AcDbSymbolTableRecord") '(100 . "AcDbLayerTableRecord")
                   (cons 2 name) '(70 . 0) (cons 62 color) '(6 . "Continuous")))))

(defun kzm-test:text (x y value)
  (entmake (list '(0 . "TEXT") '(8 . "KZM_TEST_ELEV") (list 10 x y 0.0) '(40 . 1.0) (cons 1 value))))

(defun kzm-test:mtext (x y value)
  (entmake (list '(0 . "MTEXT") '(100 . "AcDbEntity") '(8 . "KZM_TEST_ELEV") '(100 . "AcDbMText")
                 (list 10 x y 0.0) '(40 . 1.0) '(41 . 0.0) '(71 . 7) (cons 1 value))))

(defun kzm-test:point (x y)
  (entmake (list '(0 . "POINT") '(8 . "KZM_TEST_MARK") (list 10 x y 0.0))))

(defun c:KZM_TEST_DATA (/ bx by)
  (setq bx 1000.0 by 2000.0)
  (kzm-test:layer "KZM_TEST_ELEV" 3)
  (kzm-test:layer "KZM_TEST_MARK" 1)
  ;; A: TEXT with comma
  (kzm-test:text (+ bx 10.0) by "145,32")      (kzm-test:point (+ bx 9.5) by)
  ;; B: TEXT with dot (T5)
  (kzm-test:text (+ bx 20.0) by "145.32")      (kzm-test:point (+ bx 19.5) by)
  ;; C: formatted MTEXT (T5)
  (kzm-test:mtext (+ bx 30.0) by "{\\fArial|b0|i0|c204|p34;\\H1.2x;145}{\\C1;,32}")
  (kzm-test:point (+ bx 29.5) by)
  ;; D: point number "12" (T5: rejected with reason when the range is 140...160)
  (kzm-test:text (+ bx 40.0) by "12")          (kzm-test:point (+ bx 39.5) by)
  ;; E: two elevations in one MTEXT -> disputed
  (kzm-test:mtext (+ bx 50.0) by "145,40\\P146,10")
  (kzm-test:point (+ bx 49.5) by)
  ;; F: no marker within R = 1 m
  (kzm-test:text (+ bx 60.0) by "147,05")
  ;; G: point number and elevation in one text
  (kzm-test:text (+ bx 70.0) by "t.15 148,00") (kzm-test:point (+ bx 69.5) by)
  ;; H1/H2: two texts 0,005 m apart sharing one marker
  (kzm-test:text (+ bx 80.0) by "148,50")
  (kzm-test:text (+ bx 80.005) by "148,51")
  (kzm-test:point (+ bx 79.5) by)
  (command "_.ZOOM" "_E")
  (princ "\nKZM test data: 9 texts on layer KZM_TEST_ELEV, 7 points on KZM_TEST_MARK.")
  (princ))

(princ "\nLoaded. Run KZM_TEST_DATA in an empty drawing.")
(princ)
