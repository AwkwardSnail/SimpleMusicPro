// PDFium is not safe to use from two threads at once, and the engraving tests draw with it: run them one at a time.
[assembly: Xunit.CollectionBehavior(DisableTestParallelization = true)]
