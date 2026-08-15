(defun dvb-write-pair (stream pair)
  (if pair
    (write-line
      (strcat "  " (itoa (car pair)) "=" (vl-princ-to-string (cdr pair)))
      stream)))

(defun dvb-dump (/ stream selection index entity data kind)
  (setq stream
    (open
      "C:/Users/ADMIN/AppData/Local/Temp/dvb-abutment-review/dwg-dimensions.txt"
      "w"))
  (if (not stream)
    (progn (princ "\nDVB_DUMP_OPEN_FAILED") (exit)))
  (setq selection (ssget "_X" '((0 . "DIMENSION,TEXT,MTEXT,MULTILEADER"))))
  (setq index 0)
  (if selection
    (while (< index (sslength selection))
      (setq entity (ssname selection index))
      (setq data (entget entity))
      (setq kind (cdr (assoc 0 data)))
      (write-line
        (strcat "ENTITY handle=" (cdr (assoc 5 data)) " type=" kind)
        stream)
      (dvb-write-pair stream (assoc 8 data))
      (dvb-write-pair stream (assoc 1 data))
      (dvb-write-pair stream (assoc 10 data))
      (dvb-write-pair stream (assoc 11 data))
      (dvb-write-pair stream (assoc 13 data))
      (dvb-write-pair stream (assoc 14 data))
      (dvb-write-pair stream (assoc 15 data))
      (dvb-write-pair stream (assoc 16 data))
      (dvb-write-pair stream (assoc 42 data))
      (dvb-write-pair stream (assoc 70 data))
      (if (= kind "DIMENSION")
        (write-line
          (strcat "  measurement42="
            (if (assoc 42 data) (rtos (cdr (assoc 42 data)) 2 8) "<none>"))
          stream))
      (setq index (1+ index))))
  (close stream)
  (princ "\nDVB_DUMP_COMPLETE"))

(dvb-dump)
