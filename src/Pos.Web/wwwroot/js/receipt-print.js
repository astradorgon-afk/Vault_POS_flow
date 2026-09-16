// Opens a printable receipt in a new window and hands it to the browser's
// print dialog (print-to-PDF on most terminals).  Used by the web POS's
// "Printable / PDF" action on completed sale receipts.
window.vaultflowPrintReceipt = function (html) {
    var target = window.open('', 'vaultflow-print-receipt', 'width=420,height=700');
    if (!target) { return; }
    target.document.open();
    target.document.write(html);
    target.document.close();
    target.onload = function () {
        target.focus();
        setTimeout(function () { target.print(); }, 120);
    };
};