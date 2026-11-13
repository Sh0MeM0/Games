;;; Test data of the "Kartogramma" module (KZM_IMPORT, cartogram, POINT and block sources).
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
  ;; D: point number "12" (T5: a whole number is not an elevation, rejected with reason)
  (kzm-test:text (+ bx 40.0) by "12")          (kzm-test:point (+ bx 39.5) by)
  ;; E: two elevations in one MTEXT -> disputed
  (kzm-test:mtext (+ bx 50.0) by "145,40\\P146,10")
  (kzm-test:point (+ bx 49.5) by)
  ;; F: no marker of its own (the nearest marker belongs to G)
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

;;; Cartogram check (like T1): fact texts 100,00 and project texts 100,50 every 10 m
;;; over 60 x 40 m; boundary 40 x 20 m. Expected: fill 400 m3, cut 0.
(defun kzm-test:text-on (layer x y value)
  (entmake (list '(0 . "TEXT") (cons 8 layer) (list 10 x y 0.0) '(40 . 0.8) (cons 1 value))))

(defun c:KZM_TEST_CARTO (/ bx by i j)
  (setq bx 1000.0 by 2100.0)
  (kzm-test:layer "KZM_TEST_FACT" 7)
  (kzm-test:layer "KZM_TEST_PROJ" 1)
  (kzm-test:layer "KZM_TEST_BOUND" 4)
  (setq i 0)
  (while (<= i 6)
    (setq j 0)
    (while (<= j 4)
      (kzm-test:text-on "KZM_TEST_FACT" (+ bx (* i 10.0)) (+ by (* j 10.0)) "100,00")
      (kzm-test:text-on "KZM_TEST_PROJ" (+ bx (* i 10.0) 3.0) (+ by (* j 10.0) 3.0) "100,50")
      (setq j (1+ j)))
    (setq i (1+ i)))
  (entmake (list '(0 . "LWPOLYLINE") '(100 . "AcDbEntity") '(8 . "KZM_TEST_BOUND") '(100 . "AcDbPolyline")
                 '(90 . 4) '(70 . 1)
                 (list 10 (+ bx 5.0) (+ by 5.0)) (list 10 (+ bx 45.0) (+ by 5.0))
                 (list 10 (+ bx 45.0) (+ by 25.0)) (list 10 (+ bx 5.0) (+ by 25.0))))
  (command "_.ZOOM" "_E")
  (princ "\nKZM cartogram test: 35 fact texts, 35 project texts, boundary 40 x 20 m. Expected fill 400 m3, cut 0.")
  (princ))

;;; Import from POINT with Z and from blocks with an elevation attribute (version 0.5).
;;; Expected (VALIDATION.md, step 10): POINT -> 2 points (150,25 and 150,00), 1 POINT with Z = 0 skipped;
;;; blocks (tag ELEV, number tag NUM) -> 2 points numbered 21 and 22 (151,30 and 151,45), 1 rejected ("152").
(defun kzm-test:block-definition ()
  (if (not (tblsearch "BLOCK" "KZM_TEST_SURVEY"))
    (progn
      (entmake '((0 . "BLOCK") (2 . "KZM_TEST_SURVEY") (70 . 2) (10 0.0 0.0 0.0)))
      (entmake '((0 . "CIRCLE") (8 . "0") (10 0.0 0.0 0.0) (40 . 0.3)))
      (entmake '((0 . "ATTDEF") (8 . "0") (10 0.5 0.1 0.0) (40 . 0.8) (1 . "") (3 . "Elevation") (2 . "ELEV") (70 . 0)))
      (entmake '((0 . "ATTDEF") (8 . "0") (10 0.5 1.1 0.0) (40 . 0.8) (1 . "") (3 . "Number") (2 . "NUM") (70 . 0)))
      (entmake '((0 . "ENDBLK"))))))

(defun kzm-test:survey-block (x y elev num)
  (entmake (list '(0 . "INSERT") '(8 . "KZM_TEST_BLOCKS") '(66 . 1) '(2 . "KZM_TEST_SURVEY")
                 (list 10 x y 0.0) '(41 . 1.0) '(42 . 1.0) '(43 . 1.0) '(50 . 0.0)))
  (entmake (list '(0 . "ATTRIB") '(8 . "KZM_TEST_BLOCKS") (list 10 (+ x 0.5) (+ y 0.1) 0.0) '(40 . 0.8)
                 (cons 1 elev) '(2 . "ELEV") '(70 . 0)))
  (entmake (list '(0 . "ATTRIB") '(8 . "KZM_TEST_BLOCKS") (list 10 (+ x 0.5) (+ y 1.1) 0.0) '(40 . 0.8)
                 (cons 1 num) '(2 . "NUM") '(70 . 0)))
  (entmake '((0 . "SEQEND"))))

(defun c:KZM_TEST_SOURCES (/ bx by)
  (setq bx 1000.0 by 2200.0)
  (kzm-test:layer "KZM_TEST_Z" 2)
  (kzm-test:layer "KZM_TEST_BLOCKS" 5)
  (entmake (list '(0 . "POINT") '(8 . "KZM_TEST_Z") (list (+ bx 10.0) by 150.25)))
  (entmake (list '(0 . "POINT") '(8 . "KZM_TEST_Z") (list (+ bx 20.0) by 150.0)))
  (entmake (list '(0 . "POINT") '(8 . "KZM_TEST_Z") (list (+ bx 30.0) by 0.0)))
  (kzm-test:block-definition)
  (kzm-test:survey-block (+ bx 10.0) (+ by 10.0) "151.30" "21")
  (kzm-test:survey-block (+ bx 20.0) (+ by 10.0) "151,45" "22")
  (kzm-test:survey-block (+ bx 30.0) (+ by 10.0) "152" "23")
  (command "_.ZOOM" "_E")
  (princ "\nKZM sources test: 3 POINT on KZM_TEST_Z (one with Z = 0), 3 blocks KZM_TEST_SURVEY on KZM_TEST_BLOCKS.")
  (princ))

(princ "\nLoaded. Commands: KZM_TEST_DATA (import), KZM_TEST_CARTO (cartogram), KZM_TEST_SOURCES (POINT and blocks) in an empty drawing.")
(princ)
